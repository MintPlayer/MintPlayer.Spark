using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Promotions.Identity;

/// <summary>GDPR Art. 15/20: a user's waiting-list entries, back-in-stock subscriptions and tracking attribution.</summary>
internal sealed class PromotionPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser> where TUser : SparkUser
{
    public string Name => "commerce-promotions";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using var session = store.OpenAsyncSession();
        var entries = await session.Query<PromotionWaitingListEntry>().Where(e => e.UserId == userId).Take(1024).ToListAsync(cancellationToken);
        var subscriptions = await session.Query<PromotionStockSubscription>().Where(s => s.UserId == userId).Take(1024).ToListAsync(cancellationToken);
        var attribution = await session.LoadAsync<PromotionAttribution>(PromotionAttribution.IdForUser(userId), cancellationToken);
        return new
        {
            WaitingLists = entries.Select(e => new { e.ListId, Status = e.Status.ToString(), e.Quantity, e.JoinedAt, e.OfferedAt, e.OfferExpiresAt }),
            BackInStock = subscriptions.Select(s => new { s.ItemId, s.Email, s.CreatedAt, s.ExpiresAt, s.NotifiedAt }),
            Attribution = attribution is null ? null : new { attribution.Ref, attribution.UtmSource, attribution.UtmMedium, attribution.UtmCampaign, attribution.AttributedAt, attribution.ExpiresAt },
        };
    }
}

/// <summary>
/// GDPR Art. 17: waiting-list entries and back-in-stock subscriptions are deleted (GB11: "erased with the account"); an
/// open offer lapses; the attribution is deleted. Order attributions and commissions stay: they carry no personal data
/// beyond the order id, and the money audit keeps them for its retention floor (Art. 17(3)(b)).
/// </summary>
internal sealed class PromotionAccountDeletion<TUser>(IDocumentStore store, BackInStock backInStock) : ISparkAccountDeletionHandler<TUser> where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using (var session = store.OpenAsyncSession())
        {
            foreach (var entry in await session.Query<PromotionWaitingListEntry>().Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(30))).Where(e => e.UserId == userId).Take(1024).ToListAsync(cancellationToken))
            {
                if (entry.OfferCode is { } offer && await session.LoadAsync<PromotionCode>(PromotionCode.IdFor(offer), cancellationToken) is { } code)
                {
                    code.Active = false;
                    code.AssignedToUserId = "deleted";
                }
                session.Delete(entry);
            }
            await session.SaveChangesAsync(cancellationToken);
        }
        await backInStock.EraseAsync(userId, user.Email, cancellationToken);

        // The attribution is written in cluster-wide sessions only (the visitor merge is one).
        using var cluster = store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        if (await cluster.LoadAsync<PromotionAttribution>(PromotionAttribution.IdForUser(userId), cancellationToken) is { } attribution)
        {
            cluster.Delete(attribution);
            await cluster.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>X1: a guest's tracking attribution moves onto the account at sign-in (first touch kept).</summary>
internal sealed class PromotionVisitorMerge(Tracking tracking) : ISparkVisitorMergeHandler
{
    public Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken)
        => tracking.MergeAsync(visitorId, userId, session, cancellationToken);
}
