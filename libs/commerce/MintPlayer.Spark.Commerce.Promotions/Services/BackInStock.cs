using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// Back-in-stock notifications (GB11), the shop mode of the waiting list. A buyer subscribes to a variant (signed in: the
/// account's confirmed address; a guest: an address confirmed by double opt-in). When the variant's availability rises
/// above zero, subscribers are mailed oldest first, in a batch of <c>ceil(restocked × factor)</c>. No code and no
/// reservation: the first payer wins (OnPayment). Subscriptions lapse after a configurable period and are erased with
/// the account (GDPR Art. 17).
/// </summary>
internal sealed class BackInStock(
    IDocumentStore store,
    PromotionItems items,
    PromotionMails mails,
    IServiceProvider services,
    TimeProvider clock,
    IOptionsMonitor<SparkCommercePromotionsOptions> options,
    ILogger<BackInStock> logger)
{
    /// <summary>
    /// Subscribes to <paramref name="itemId"/>. Idempotent per item and address. A guest's subscription is confirmed by the
    /// link mailed to it; a signed-in buyer's needs no confirmation when the account's address is confirmed.
    /// </summary>
    /// <returns>The subscription and whether a confirmation mail went out.</returns>
    public async Task<(PromotionStockSubscription Subscription, bool ConfirmationSent)> SubscribeAsync(
        string itemId, string? userId, string? guestEmail, string? culture, CancellationToken cancellationToken)
    {
        var bis = options.CurrentValue.BackInStock;
        if (!bis.Enabled)
            throw new PromotionException(PromotionRefusals.InvalidRequest, "disabled");
        var item = await items.ResolveAsync(itemId, 1, cancellationToken) ?? throw new PromotionException(PromotionRefusals.ItemNotFound, itemId);
        if (item.HasNumberedUnits)
            throw new PromotionException(PromotionRefusals.NumberedUnits, itemId); // tickets use the waiting list (ticket mode)

        string email;
        var confirmed = false;
        if (userId is not null)
        {
            var (accountEmail, _, accountCulture) = await mails.AccountAsync(userId, cancellationToken);
            if (accountEmail is null)
                throw new PromotionException(PromotionRefusals.InvalidRequest, "email"); // the account has no confirmed address
            email = accountEmail;
            culture ??= accountCulture;
            confirmed = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(guestEmail) || !System.Net.Mail.MailAddress.TryCreate(guestEmail.Trim(), out var parsed))
                throw new PromotionException(PromotionRefusals.InvalidRequest, "email");
            email = parsed.Address;
        }

        var now = clock.GetUtcNow();
        var token = PromotionHashes.NewToken();
        using var session = store.OpenAsyncSession();
        var id = PromotionStockSubscription.IdFor(itemId, email);
        var subscription = await session.LoadAsync<PromotionStockSubscription>(id, cancellationToken);
        var sendConfirmation = false;
        if (subscription is null || subscription.ExpiresAt <= now || subscription.NotifiedAt is not null)
        {
            if (subscription is not null)
                session.Delete(subscription);
            subscription = new PromotionStockSubscription
            {
                Id = id,
                ItemId = itemId,
                OrganizationId = item.OrganizationId,
                UserId = userId,
                Email = email.ToLowerInvariant(),
                Culture = culture,
                Confirmed = confirmed,
                ConfirmedAt = confirmed ? now : null,
                TokenHash = PromotionHashes.Token(token),
                CreatedAt = now,
                ExpiresAt = now + bis.SubscriptionLifetime,
            };
            await session.StoreAsync(subscription, id, cancellationToken);
            sendConfirmation = !confirmed;
        }
        else if (!subscription.Confirmed)
        {
            if (confirmed)
            {
                subscription.Confirmed = true;
                subscription.ConfirmedAt = now;
                subscription.UserId = userId;
            }
            else
            {
                // A repeated guest request re-sends the confirmation with a fresh token.
                subscription.TokenHash = PromotionHashes.Token(token);
                sendConfirmation = true;
            }
        }
        await session.SaveChangesAsync(cancellationToken);
        if (sendConfirmation)
            await mails.SendConfirmationAsync(subscription, item.Title.GetValue(PromotionMails.Language(culture)), token, cancellationToken);
        return (subscription, sendConfirmation);
    }

    /// <summary>The guest followed the confirmation link (double opt-in).</summary>
    public async Task<bool> ConfirmAsync(string id, string token, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var subscription = await session.LoadAsync<PromotionStockSubscription>(id, cancellationToken);
        if (subscription is null || !PromotionHashes.Matches(token, subscription.TokenHash))
            return false;
        if (!subscription.Confirmed)
        {
            subscription.Confirmed = true;
            subscription.ConfirmedAt = clock.GetUtcNow();
            await session.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    /// <summary>Ends a subscription with the token of its mail (no sign-in needed).</summary>
    public async Task<bool> UnsubscribeAsync(string id, string token, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var subscription = await session.LoadAsync<PromotionStockSubscription>(id, cancellationToken);
        if (subscription is null || !PromotionHashes.Matches(token, subscription.TokenHash))
            return false;
        session.Delete(subscription);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The signed-in buyer's subscriptions (the account area, GB10).</summary>
    public async Task<List<PromotionStockSubscription>> MineAsync(string userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<PromotionStockSubscription>().Where(s => s.UserId == userId).Take(256).ToListAsync(cancellationToken);
    }

    /// <summary>Deletes one of the signed-in buyer's own subscriptions.</summary>
    public async Task<bool> DeleteMineAsync(string userId, string id, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var subscription = await session.LoadAsync<PromotionStockSubscription>(id, cancellationToken);
        if (subscription is null || !string.Equals(subscription.UserId, userId, StringComparison.Ordinal))
            return false;
        session.Delete(subscription);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The job's pass: lapse old and unconfirmed subscriptions, then, per item with waiting subscribers, mail a batch on a
    /// restock (availability rising from zero).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var bis = options.CurrentValue.BackInStock;
        var now = clock.GetUtcNow();
        await ExpireAsync(now, bis, cancellationToken);

        List<string> itemIds;
        using (var session = store.OpenAsyncSession())
        {
            itemIds = (await session.Query<PromotionStockSubscription>().Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                    .Where(s => s.Confirmed && s.NotifiedAt == null && s.ExpiresAt > now)
                    .Select(s => s.ItemId)
                    .Take(4096)
                    .ToListAsync(cancellationToken))
                .Distinct(StringComparer.Ordinal).Take(256).ToList();
        }
        foreach (var itemId in itemIds)
        {
            try
            {
                await NotifyItemAsync(itemId, now, bis, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Back-in-stock pass for {ItemId} failed.", itemId);
            }
        }
    }

    private async Task NotifyItemAsync(string itemId, DateTimeOffset now, BackInStockOptions bis, CancellationToken cancellationToken)
    {
        var item = await items.ResolveAsync(itemId, 1, cancellationToken);
        if (item is null || await items.AvailabilityAsync(item, cancellationToken) is not { } availability)
            return;

        using var session = store.OpenAsyncSession();
        var watchId = PromotionStockWatch.IdFor(itemId);
        var watch = await session.LoadAsync<PromotionStockWatch>(watchId, cancellationToken);
        if (watch is null)
        {
            watch = new PromotionStockWatch { Id = watchId, ItemId = itemId, LastAvailable = 0 };
            await session.StoreAsync(watch, watchId, cancellationToken);
        }
        var restocked = watch.LastAvailable <= 0 && availability.Available > 0 ? availability.Available : 0;
        watch.LastAvailable = availability.Available;
        if (restocked > 0)
        {
            watch.LastRestockAt = now;
            var batch = PromotionRules.BackInStockBatch(restocked, bis.BatchFactor, bis.MaxBatch);
            // By id prefix (consistent, no index): a subscriber is never skipped because an index lagged.
            var subscribers = (await LoadForItemAsync(session, itemId, cancellationToken))
                .Where(s => s.Confirmed && s.NotifiedAt == null && s.ExpiresAt > now)
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal)
                .Take(batch)
                .ToList();
            var consent = bis.RequireMarketingConsent ? services.GetService<ISparkConsent>() : null;
            foreach (var subscription in subscribers)
            {
                if (bis.RequireMarketingConsent)
                {
                    var subject = subscription.UserId is { } user ? SparkConsentSubject.Account(user) : SparkConsentSubject.Email(subscription.Email);
                    if (consent is null || !await consent.HasAsync(subject, SparkConsentPurposes.MarketingMail, cancellationToken))
                        continue;
                }
                // A fresh token for the unsubscribe link: the stored hash changes with it.
                var token = PromotionHashes.NewToken();
                subscription.TokenHash = PromotionHashes.Token(token);
                subscription.NotifiedAt = now;
                await mails.SendBackInStockAsync(subscription, item.Title.GetValue(PromotionMails.Language(subscription.Culture)), token, cancellationToken);
            }
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<List<PromotionStockSubscription>> LoadForItemAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string itemId, CancellationToken cancellationToken)
    {
        var prefix = $"PromotionStockSubscriptions/{itemId}/";
        var result = new List<PromotionStockSubscription>();
        var start = 0;
        while (true)
        {
            var page = (await session.Advanced.LoadStartingWithAsync<PromotionStockSubscription>(prefix, start: start, pageSize: 1024, token: cancellationToken)).ToList();
            result.AddRange(page);
            if (page.Count < 1024)
                return result;
            start += page.Count;
        }
    }

    private async Task ExpireAsync(DateTimeOffset now, BackInStockOptions bis, CancellationToken cancellationToken)
    {
        var unconfirmedBefore = now - bis.ConfirmationWindow;
        using var session = store.OpenAsyncSession();
        var stale = await session.Query<PromotionStockSubscription>()
            .Where(s => s.ExpiresAt <= now || (!s.Confirmed && s.CreatedAt <= unconfirmedBefore))
            .Take(512)
            .ToListAsync(cancellationToken);
        foreach (var subscription in stale)
            session.Delete(subscription);
        if (stale.Count > 0)
            await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>GDPR Art. 17: the account's subscriptions (by user id and by its address) are deleted.</summary>
    public async Task EraseAsync(string userId, string? email, CancellationToken cancellationToken)
    {
        var address = email?.Trim().ToLowerInvariant();
        using var session = store.OpenAsyncSession();
        var mine = await session.Query<PromotionStockSubscription>().Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(30))).Where(s => s.UserId == userId).Take(1024).ToListAsync(cancellationToken);
        if (address is { Length: > 0 })
            mine.AddRange((await session.Query<PromotionStockSubscription>().Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(30))).Where(s => s.Email == address).Take(1024).ToListAsync(cancellationToken))
                .Where(s => mine.All(m => m.Id != s.Id)));
        foreach (var subscription in mine)
            session.Delete(subscription);
        await session.SaveChangesAsync(cancellationToken);
    }
}
