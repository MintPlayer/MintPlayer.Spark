using System.Security.Cryptography;
using MintPlayer.Spark.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// The waiting list, ticket mode (§4.6, D18). <b>Activation rule:</b> the item is sold out, its queue has closed, and no
/// holds are outstanding; until then freed capacity goes to the queue (or the public). Once active, every unit freed
/// from public sale (a refund, a lapsed hold, a released block) moves into the list's block, and the block's spare units
/// are offered in order of joining as personal, time-limited codes. An offer that lapses goes to the next entry, never
/// back to the public, while the list is active. Closing the list returns the spare units to public sale.
/// </summary>
internal sealed class WaitingLists(
    IDocumentStore store,
    PromotionItems items,
    PromotionBlocks blocks,
    PromotionMails mails,
    IServiceProvider services,
    TimeProvider clock,
    IOptionsMonitor<SparkCommercePromotionsOptions> options,
    ILogger<WaitingLists> logger)
{
    /// <summary>Creates a list for <paramref name="itemId"/>.</summary>
    public async Task<PromotionWaitingList> CreateAsync(string itemId, string? organizationId, TranslatedString? title, string? queueScope, TimeSpan? offerValidity, CancellationToken cancellationToken)
    {
        var item = await items.ResolveAsync(itemId, 1, cancellationToken) ?? throw new PromotionException(PromotionRefusals.ItemNotFound, itemId);
        if (item.HasNumberedUnits)
            throw new PromotionException(PromotionRefusals.NumberedUnits, itemId);
        if (!string.Equals(item.OrganizationId, organizationId, StringComparison.Ordinal))
            throw new PromotionException(PromotionRefusals.InvalidRequest, "organization");
        var list = new PromotionWaitingList
        {
            Id = "PromotionWaitingLists/" + Guid.NewGuid().ToString("N"),
            ItemId = itemId,
            OrganizationId = organizationId,
            Title = title ?? item.Title,
            QueueScope = queueScope,
            OfferValidity = offerValidity,
            CreatedAt = clock.GetUtcNow(),
        };
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(list, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return list;
    }

    /// <summary>Joins (or changes the quantity of) the signed-in buyer's entry. Ticket mode needs an account.</summary>
    public async Task<PromotionWaitingListEntry> JoinAsync(string listId, string userId, int quantity, CancellationToken cancellationToken)
    {
        if (quantity < 1 || quantity > options.CurrentValue.WaitingList.MaxQuantityPerEntry)
            throw new PromotionException(PromotionRefusals.QuantityExceeded);
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var list = await session.LoadAsync<PromotionWaitingList>(listId, cancellationToken);
        if (list is null || list.Status == PromotionWaitingListStatus.Closed)
            throw new PromotionException(PromotionRefusals.ListClosed);
        var id = PromotionWaitingListEntry.IdFor(listId, userId);
        var entry = await session.LoadAsync<PromotionWaitingListEntry>(id, cancellationToken);
        if (entry is null || entry.Status is PromotionWaitingListEntryStatus.Left)
        {
            list.EntrySequence++;
            entry ??= new PromotionWaitingListEntry { Id = id, ListId = listId, UserId = userId };
            entry.Position = list.EntrySequence;
            entry.Status = PromotionWaitingListEntryStatus.Waiting;
            entry.JoinedAt = clock.GetUtcNow();
            entry.OfferCode = null;
            entry.OfferedAt = entry.OfferExpiresAt = null;
            await session.StoreAsync(entry, cancellationToken);
        }
        if (entry.Status == PromotionWaitingListEntryStatus.Waiting)
            entry.Quantity = quantity;
        await session.SaveChangesAsync(cancellationToken);
        return entry;
    }

    /// <summary>Leaves the list; an open offer lapses at once (its units go to the next entry).</summary>
    public async Task<bool> LeaveAsync(string listId, string userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var entry = await session.LoadAsync<PromotionWaitingListEntry>(PromotionWaitingListEntry.IdFor(listId, userId), cancellationToken);
        if (entry is null || entry.Status is PromotionWaitingListEntryStatus.Left or PromotionWaitingListEntryStatus.Purchased)
            return false;
        if (entry.OfferCode is { } offer && await session.LoadAsync<PromotionCode>(PromotionCode.IdFor(offer), cancellationToken) is { } code)
            code.Active = false;
        entry.Status = PromotionWaitingListEntryStatus.Left;
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Marks the queue closed (staff, or a bridge over the waiting room).</summary>
    public async Task CloseQueueAsync(string listId, CancellationToken cancellationToken)
    {
        using (var session = store.OpenAsyncSession())
        {
            var list = await session.LoadAsync<PromotionWaitingList>(listId, cancellationToken);
            if (list is null || list.QueueClosedAt is not null)
                return;
            list.QueueClosedAt = clock.GetUtcNow();
            await session.SaveChangesAsync(cancellationToken);
        }
        await EvaluateAsync(listId, cancellationToken);
    }

    /// <summary>Ends the list: open offers are deactivated, spare units go back to public sale.</summary>
    public async Task CloseAsync(string listId, CancellationToken cancellationToken)
    {
        string? blockId;
        using (var session = store.OpenAsyncSession())
        {
            var list = await session.LoadAsync<PromotionWaitingList>(listId, cancellationToken);
            if (list is null)
                return;
            blockId = list.BlockId;
            if (list.Status != PromotionWaitingListStatus.Closed)
            {
                list.Status = PromotionWaitingListStatus.Closed;
                list.ClosedAt = clock.GetUtcNow();
                var offered = (await LoadEntriesAsync(session, listId, cancellationToken)).Where(e => e.Status == PromotionWaitingListEntryStatus.Offered).ToList();
                foreach (var entry in offered)
                    entry.Status = PromotionWaitingListEntryStatus.OfferExpired;
                await DeactivateOffersAsync(session, offered, cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        if (blockId is not null)
            await blocks.CloseAsync(blockId, cancellationToken);
    }

    /// <summary>Re-evaluates every open or active list of the given items (a refund, a lapsed hold freed capacity).</summary>
    public async Task EvaluateForItemsAsync(IEnumerable<string> itemIds, CancellationToken cancellationToken)
    {
        var ids = itemIds.Select(PromotionItemIds.Inner).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return;
        List<string> lists;
        using (var session = store.OpenAsyncSession())
        {
            lists = await session.Query<PromotionWaitingList>()
                .Where(l => l.ItemId.In(ids) && l.Status != PromotionWaitingListStatus.Closed)
                .Select(l => l.Id!)
                .Take(256)
                .ToListAsync(cancellationToken);
        }
        foreach (var listId in lists)
            await EvaluateAsync(listId, cancellationToken);
    }

    /// <summary>
    /// One evaluation: activate when the rule holds; when active, move freed public units into the block, lapse expired
    /// offers and offer spare units in order. Idempotent; the job runs it every few seconds for every open list.
    /// </summary>
    public async Task EvaluateAsync(string listId, CancellationToken cancellationToken)
    {
        PromotionWaitingList? list;
        using (var session = store.OpenAsyncSession())
            list = await session.LoadAsync<PromotionWaitingList>(listId, cancellationToken);
        if (list is null || list.Status == PromotionWaitingListStatus.Closed)
            return;
        var item = await items.ResolveAsync(list.ItemId, 1, cancellationToken);
        if (item is null || await items.AvailabilityAsync(item, cancellationToken) is not { } availability)
            return;

        if (list.Status == PromotionWaitingListStatus.Open)
        {
            if (!PromotionRules.ShouldActivate(availability.Available, availability.Held, await QueueClosedAsync(list, cancellationToken)))
                return;
            var block = await blocks.CreateAsync(list.Id!, list.OrganizationId, list.ItemId, 0, null, cancellationToken);
            using var session = store.OpenAsyncSession();
            var fresh = await session.LoadAsync<PromotionWaitingList>(listId, cancellationToken);
            if (fresh is null || fresh.Status != PromotionWaitingListStatus.Open)
                return;
            fresh.Status = PromotionWaitingListStatus.Active;
            fresh.ActivatedAt = clock.GetUtcNow();
            fresh.BlockId = block.Id;
            await session.SaveChangesAsync(cancellationToken);
            list = fresh;
            logger.LogInformation("Waiting list {ListId} activated: {ItemId} sold out, queue closed, no holds.", listId, list.ItemId);
        }

        // Freed public units go to the list, not back to sale (§4.6).
        if (availability.Available > 0)
            await blocks.GrowAsync(list.BlockId!, availability.Available, cancellationToken);
        await OfferAsync(list, cancellationToken);
    }

    private async Task<bool> QueueClosedAsync(PromotionWaitingList list, CancellationToken cancellationToken)
    {
        if (list.QueueScope is null || list.QueueClosedAt is not null)
            return true;
        var source = services.GetServices<IWaitingListQueueState>().FirstOrDefault(s => s.Handles(list.QueueScope));
        return source is not null && await source.IsQueueClosedAsync(list.QueueScope, cancellationToken);
    }

    /// <summary>Lapses expired offers and offers the block's spare units to the next entries, strictly in order.</summary>
    private async Task OfferAsync(PromotionWaitingList list, CancellationToken cancellationToken)
    {
        var block = await blocks.LoadAsync(list.BlockId!, cancellationToken);
        if (block is null)
            return;
        var now = clock.GetUtcNow();
        var validity = list.OfferValidity ?? options.CurrentValue.WaitingList.OfferValidity;
        var toMail = new List<(PromotionWaitingListEntry Entry, string Code, DateTimeOffset ExpiresAt)>();
        using (var session = store.OpenAsyncSession())
        {
            // Entries load by id prefix, not by a query: an index could be stale, and a missed offer would be offered twice.
            var entries = await LoadEntriesAsync(session, list.Id!, cancellationToken);
            var offered = entries.Where(e => e.Status == PromotionWaitingListEntryStatus.Offered).ToList();
            var lapsed = offered.Where(e => e.OfferExpiresAt <= now).ToList();
            foreach (var entry in lapsed)
                entry.Status = PromotionWaitingListEntryStatus.OfferExpired;
            await DeactivateOffersAsync(session, lapsed, cancellationToken);
            var outstanding = offered.Where(e => e.Status == PromotionWaitingListEntryStatus.Offered).Sum(e => e.Quantity);
            var spare = (await blocks.AvailabilityAsync(block, cancellationToken)).Available - outstanding;

            if (spare > 0)
            {
                var waiting = entries.Where(e => e.Status == PromotionWaitingListEntryStatus.Waiting).OrderBy(e => e.Position);
                foreach (var entry in waiting)
                {
                    if (entry.Quantity > spare)
                        break; // strictly in order: the next entry waits for enough units
                    var code = NewOfferCode();
                    var expiresAt = now + validity;
                    await session.StoreAsync(new PromotionCode
                    {
                        Id = PromotionCode.IdFor(code),
                        Code = code,
                        OrganizationId = list.OrganizationId,
                        Effect = PromotionEffect.CapacityBlock,
                        ItemIds = [list.ItemId],
                        BlockId = block.Id,
                        AssignedToUserId = entry.UserId,
                        MaxUses = 1,
                        MaxQuantityPerOrder = entry.Quantity,
                        ValidFrom = now,
                        ValidUntil = expiresAt,
                        WaitingListEntryId = entry.Id,
                        CreatedAt = now,
                        UpdatedAt = now,
                    }, changeVector: string.Empty, PromotionCode.IdFor(code), cancellationToken);
                    entry.Status = PromotionWaitingListEntryStatus.Offered;
                    entry.OfferCode = code;
                    entry.OfferedAt = now;
                    entry.OfferExpiresAt = expiresAt;
                    spare -= entry.Quantity;
                    toMail.Add((entry, code, expiresAt));
                }
            }
            await session.SaveChangesAsync(cancellationToken);
        }
        foreach (var (entry, code, expiresAt) in toMail)
            await mails.SendOfferAsync(entry, list, code, expiresAt, cancellationToken);
    }

    /// <summary>Marks the entry of an offer code purchased (its order was confirmed).</summary>
    public async Task MarkPurchasedAsync(string entryId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var entry = await session.LoadAsync<PromotionWaitingListEntry>(entryId, cancellationToken);
        if (entry is null || entry.Status == PromotionWaitingListEntryStatus.Purchased)
            return;
        entry.Status = PromotionWaitingListEntryStatus.Purchased;
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Deactivates the offer codes of <paramref name="entries"/>, in one load.</summary>
    private static async Task DeactivateOffersAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, IEnumerable<PromotionWaitingListEntry> entries, CancellationToken cancellationToken)
    {
        var ids = entries.Where(e => e.OfferCode is not null).Select(e => PromotionCode.IdFor(e.OfferCode!)).ToList();
        if (ids.Count == 0)
            return;
        foreach (var code in (await session.LoadAsync<PromotionCode>(ids, cancellationToken)).Values)
        {
            if (code is not null)
                code.Active = false;
        }
    }

    /// <summary>Every entry of a list, by id prefix (consistent, no index). A list of many thousands is paged through each pass.</summary>
    internal static async Task<List<PromotionWaitingListEntry>> LoadEntriesAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string listId, CancellationToken cancellationToken)
    {
        var prefix = $"PromotionWaitingListEntries/{listId.Split('/')[^1]}/";
        var result = new List<PromotionWaitingListEntry>();
        var start = 0;
        while (true)
        {
            var page = (await session.Advanced.LoadStartingWithAsync<PromotionWaitingListEntry>(prefix, start: start, pageSize: 1024, token: cancellationToken)).ToList();
            result.AddRange(page);
            if (page.Count < 1024)
                return result;
            start += page.Count;
        }
    }

    /// <summary>A personal offer code: "WL" and 12 characters from an unambiguous alphabet (≈ 60 bits).</summary>
    internal static string NewOfferCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return "WL" + new string(chars);
    }
}
