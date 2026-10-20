using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Pricing;

/// <summary>
/// The append-only price history (GA3). One <see cref="CommercePriceChange"/> document per change, with an id under the
/// item's (<c>CommercePriceChanges/{itemId}/{ticks}</c>), so the history of an item is a prefix scan: no index needed,
/// and no string is spliced into a query (the prefix is a parameter of <c>LoadStartingWithAsync</c>).
/// </summary>
internal sealed class SparkPriceHistory(
    IDocumentStore store,
    IFinalPriceCalculator calculator,
    ICommerceAudit audit,
    IOptions<SparkCommerceOptions> options,
    TimeProvider clock,
    ISparkCurrentUser? currentUser = null) : ISparkPriceHistory
{
    internal const string Collection = "CommercePriceChanges";

    internal static string Prefix(string itemId) => $"{Collection}/{itemId}/";

    public async Task<PriceHistoryEntry> RecordAsync(IAsyncDocumentSession session, ISellableItem item, Money? previous, string reason,
        bool isAnnouncedReduction = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.Id))
            throw new InvalidOperationException("A sellable item needs its id before its price history is recorded (store it first).");
        var unitPrice = item.Price ?? throw new InvalidOperationException($"Sellable item '{item.Id}' has no price.");

        var now = clock.GetUtcNow();
        var after = calculator.Calculate(FinalPriceRequest.For(item)).UnitTotal;
        Money? prior = null;
        if (isAnnouncedReduction)
        {
            // PID Art. 6a(2): the prior price is the lowest price of at least the 30 days before the reduction.
            prior = await LowestAsync(session, item.Id, now, options.Value.Pricing.PriorPriceWindow, cancellationToken);
            prior ??= previous;
        }

        var actor = currentUser?.Id;
        var change = new CommercePriceChange
        {
            Id = $"{Prefix(item.Id)}{now.UtcTicks:D19}",
            ItemId = item.Id,
            OrganizationId = item.OrganizationId,
            Channel = item.Channel,
            At = now,
            Before = previous,
            After = after,
            UnitPrice = unitPrice,
            Reason = reason,
            PriorPrice = prior,
            IsAnnouncedReduction = isAnnouncedReduction,
            ChangedBy = actor,
        };
        await session.StoreAsync(change, change.Id, cancellationToken);
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = CommerceAuditActions.PriceChanged,
            SubjectId = item.Id,
            OrganizationId = item.OrganizationId,
            Before = previous,
            After = after,
            Reason = reason,
            ActorId = actor,
            At = now,
        }, cancellationToken);
        return change.ToEntry();
    }

    public async Task<Money?> GetLowestFinalPriceAsync(string itemId, DateTimeOffset before, TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        return await LowestAsync(session, itemId, before, window ?? options.Value.Pricing.PriorPriceWindow, cancellationToken);
    }

    /// <summary>
    /// The lowest final price in force at any moment of [<paramref name="before"/> − window, <paramref name="before"/>):
    /// the price in force when the window opened counts too (it applied during the window until its next change).
    /// </summary>
    internal static async Task<Money?> LowestAsync(IAsyncDocumentSession session, string itemId, DateTimeOffset before, TimeSpan window, CancellationToken ct)
    {
        var from = before - window;
        var changes = new List<CommercePriceChange>();
        var start = 0;
        while (true)
        {
            var page = await session.Advanced.LoadStartingWithAsync<CommercePriceChange>(Prefix(itemId), start: start, pageSize: 1024, token: ct);
            var list = page.ToList();
            changes.AddRange(list);
            if (list.Count < 1024) break;
            start += list.Count;
        }
        return Lowest(changes.Select(c => c.ToEntry()), from, before);
    }

    /// <summary>Pure core of <see cref="LowestAsync"/>, for tests.</summary>
    internal static Money? Lowest(IEnumerable<PriceHistoryEntry> history, DateTimeOffset from, DateTimeOffset before)
    {
        var ordered = history.Where(e => e.At < before).OrderBy(e => e.At).ToList();
        Money? lowest = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var until = i + 1 < ordered.Count ? ordered[i + 1].At : before;
            if (until <= from) continue; // replaced before the window opened
            if (lowest is null || entry.After < lowest) lowest = entry.After;
        }
        return lowest;
    }
}
