using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Promotions.Contracts;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// Promoter and affiliate tracking links (GC2). A visit with <c>?ref=</c> (and UTM parameters) is attributed to the
/// visitor, first touch within the window, and only with consent where the host requires it (GA9, X21: an attribution is
/// analytics, not strictly necessary). The attribution sits beside the basket (same identity) and is copied onto the
/// order when it is placed; confirmed orders book the link's commission, refunds and lost disputes reverse it.
/// </summary>
internal sealed class Tracking(
    IDocumentStore store,
    IServiceProvider services,
    ICommerceAudit audit,
    ISparkPlatformFeeLedger ledger,
    TimeProvider clock,
    IOptionsMonitor<SparkCommercePromotionsOptions> options)
{
    /// <summary>The booker name of Promotions' commissions in the platform-fee ledger (the library alias).</summary>
    internal const string LedgerBooker = "commerce-promotions";

    /// <summary>
    /// Records the current visitor's attribution to <paramref name="request"/>'s link. False (nothing stored) when tracking
    /// is off, the link is unknown or inactive, consent is required and missing, or an earlier attribution still counts.
    /// </summary>
    public async Task<bool> CaptureAsync(SparkTrackRequest request, CancellationToken cancellationToken)
    {
        var tracking = options.CurrentValue.Tracking;
        if (!tracking.Enabled || PromotionRules.NormalizeCode(request.Ref) is not { } reference)
            return false;
        if (tracking.RequireConsent && (services.GetService<ISparkConsent>() is not { } consent || !await consent.HasAsync(tracking.ConsentPurpose, cancellationToken)))
            return false;
        PromotionTrackingLink? link;
        using (var read = store.OpenAsyncSession())
            link = await read.LoadAsync<PromotionTrackingLink>(PromotionTrackingLink.IdFor(reference), cancellationToken);
        if (link is not { Active: true })
            return false;

        var visitor = services.GetRequiredService<ISparkVisitor>();
        string id;
        if (visitor.UserId is { Length: > 0 } userId)
            id = PromotionAttribution.IdForUser(userId);
        else
            id = PromotionAttribution.IdForVisitor(await visitor.EnsureAsync());

        var now = clock.GetUtcNow();
        // Written in a cluster-wide session only, like the basket: the visitor merge moves it in one.
        using var session = store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        var existing = await session.LoadAsync<PromotionAttribution>(id, cancellationToken);
        if (!PromotionRules.ReplacesAttribution(existing, now))
            return false;
        var attribution = existing ?? new PromotionAttribution { Id = id };
        attribution.Ref = reference;
        attribution.UtmSource = Clip(request.UtmSource ?? link.UtmSource);
        attribution.UtmMedium = Clip(request.UtmMedium ?? link.UtmMedium);
        attribution.UtmCampaign = Clip(request.UtmCampaign ?? link.UtmCampaign);
        attribution.UtmContent = Clip(request.UtmContent ?? link.UtmContent);
        attribution.AttributedAt = now;
        attribution.ExpiresAt = now + tracking.AttributionWindow;
        if (existing is null)
            await session.StoreAsync(attribution, id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Copies the buyer's live attribution onto a placed order (idempotent).</summary>
    public async Task OnPlacedAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        var attributionId = order.BuyerUserId is { } user ? PromotionAttribution.IdForUser(user)
            : order.VisitorId is { } visitor ? PromotionAttribution.IdForVisitor(visitor) : null;
        if (attributionId is null)
            return;
        using var session = store.OpenAsyncSession();
        var id = PromotionOrderAttribution.IdFor(order.Id!);
        if (await session.LoadAsync<PromotionOrderAttribution>(id, cancellationToken) is not null)
            return;
        var attribution = await session.LoadAsync<PromotionAttribution>(attributionId, cancellationToken);
        if (attribution is null || attribution.ExpiresAt <= order.CreatedAt)
            return;
        var link = await session.LoadAsync<PromotionTrackingLink>(PromotionTrackingLink.IdFor(attribution.Ref), cancellationToken);
        if (link is null)
            return;
        await session.StoreAsync(new PromotionOrderAttribution
        {
            Id = id,
            OrderId = order.Id!,
            OrderNumber = order.Number,
            Ref = attribution.Ref,
            OrganizationId = link.OrganizationId,
            UtmSource = attribution.UtmSource,
            UtmMedium = attribution.UtmMedium,
            UtmCampaign = attribution.UtmCampaign,
            UtmContent = attribution.UtmContent,
            AttributedTotal = AttributedTotal(order, link.OrganizationId),
            Status = PromotionOrderAttributionStatus.Placed,
            AttributedAt = attribution.AttributedAt,
            PlacedAt = order.CreatedAt,
        }, changeVector: string.Empty, id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A confirmed order: the attribution counts, and the link's commission is booked once.</summary>
    public async Task OnConfirmedAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var attribution = await session.LoadAsync<PromotionOrderAttribution>(PromotionOrderAttribution.IdFor(order.Id!), cancellationToken);
        if (attribution is null)
            return;
        var link = await session.LoadAsync<PromotionTrackingLink>(PromotionTrackingLink.IdFor(attribution.Ref), cancellationToken);
        attribution.Status = PromotionOrderAttributionStatus.Confirmed;
        attribution.ConfirmedAt ??= clock.GetUtcNow();
        attribution.AttributedTotal = AttributedTotal(order, attribution.OrganizationId);

        if (link is not null)
        {
            var amount = PromotionRules.Commission(link, attribution.AttributedTotal);
            // Booked into Payments' platform-fee ledger (GB4) in this session, with the audit entry or not at all. The
            // ledger's natural id makes a redelivered Confirmed event book nothing (null).
            var booked = await ledger.BookCommissionAsync(session, new CommissionBooking
            {
                Booker = LedgerBooker,
                SourceId = order.Id!,
                Amount = amount,
                SellerOrganizationId = link.OrganizationId,
                Reference = order.Id,
                BeneficiaryId = link.PromoterUserId,
                AttributionRef = link.Ref,
                OccurredAt = clock.GetUtcNow(),
            }, cancellationToken);
            if (booked is not null)
            {
                await audit.RecordAsync(session, new CommerceAuditEntry
                {
                    Action = PromotionAuditActions.CommissionBooked,
                    SubjectId = order.Id!,
                    OrganizationId = link.OrganizationId,
                    After = amount,
                    Data = new Dictionary<string, string> { ["ref"] = link.Ref },
                }, cancellationToken);
            }
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A refund, a refused line or a lost dispute: the commission is reversed in proportion to what the order gave back,
    /// keyed by the event's reference (a redelivered event books nothing twice).
    /// </summary>
    public async Task OnReversedAsync(CheckoutOrder order, string reference, bool full, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var commission = await ledger.GetCommissionAsync(session, LedgerBooker, order.Id!, cancellationToken);
        if (commission is null)
            return;
        // Target: the share of the commission the refunded money stands for (all of it on a full return). The ledger
        // books only what earlier reversals have not taken back yet, once per reference.
        var target = ReversalTarget(commission.Amount, order, full);
        var delta = await ledger.ReverseCommissionAsync(session, LedgerBooker, order.Id!, reference, target, clock.GetUtcNow(), cancellationToken);
        if (delta.ToMinorUnits() <= 0)
            return;
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = PromotionAuditActions.CommissionReversed,
            SubjectId = order.Id!,
            OrganizationId = commission.SellerOrganizationId,
            After = -delta,
            Reason = reference,
            Data = new Dictionary<string, string> { ["ref"] = commission.AttributionRef ?? "" },
        }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// How much of <paramref name="commission"/> should be reversed in all after this event: everything on a full
    /// return (or an order without a total), otherwise the refunded share of the order total (Money.Share, rounded once).
    /// </summary>
    internal static Money ReversalTarget(Money commission, CheckoutOrder order, bool full)
        => full || order.Total.ToMinorUnits() <= 0
            ? commission
            : commission.Share(Math.Min(order.AmountRefunded?.ToMinorUnits() ?? 0, order.Total.ToMinorUnits()), order.Total.ToMinorUnits());

    /// <summary>An order that ended unpaid keeps its attribution as cancelled (the report counts placed vs confirmed).</summary>
    public async Task OnCancelledAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var attribution = await session.LoadAsync<PromotionOrderAttribution>(PromotionOrderAttribution.IdFor(order.Id!), cancellationToken);
        if (attribution is null || attribution.Status == PromotionOrderAttributionStatus.Confirmed)
            return;
        attribution.Status = PromotionOrderAttributionStatus.Cancelled;
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The per-link report (GC2): orders placed and confirmed, attributed sales and commissions, by currency.</summary>
    public async Task<SparkTrackingReport> ReportAsync(string reference, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var attributions = new List<PromotionOrderAttribution>();
        var skip = 0;
        while (true)
        {
            var page = await session.Query<PromotionOrderAttribution>().Where(a => a.Ref == reference).Skip(skip).Take(1024).ToListAsync(cancellationToken);
            attributions.AddRange(page);
            if (page.Count < 1024)
                break;
            skip += page.Count;
        }
        var commissions = await ledger.ListCommissionsAsync(session, reference, cancellationToken);
        var confirmed = attributions.Where(a => a.Status == PromotionOrderAttributionStatus.Confirmed).ToList();
        var sales = confirmed.GroupBy(a => a.AttributedTotal.Currency)
            .Select(g => Money.Sum(g.Select(a => a.AttributedTotal), g.Key)).ToList();
        // Commissions minus their reversals, per currency (ledger amounts are positive; the kind gives the sign).
        var commissionTotals = commissions.GroupBy(c => c.Amount.Currency)
            .Select(g => Money.FromMinorUnits(g.Sum(c => c.SignedCommissionMinorUnits), g.Key)).ToList();
        return new SparkTrackingReport(reference, attributions.Count, confirmed.Count,
            attributions.Count(a => a.Status == PromotionOrderAttributionStatus.Cancelled), sales, commissionTotals,
            attributions.GroupBy(a => a.UtmSource ?? "").Select(g => new SparkTrackingSourceCount(g.Key, g.Count())).ToList());
    }

    /// <summary>X1: a guest's attribution moves onto the account at sign-in, unless the account has a live one (first touch).</summary>
    public async Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var guest = await session.LoadAsync<PromotionAttribution>(PromotionAttribution.IdForVisitor(visitorId), cancellationToken);
        if (guest is null)
            return;
        var accountId = PromotionAttribution.IdForUser(userId);
        var account = await session.LoadAsync<PromotionAttribution>(accountId, cancellationToken);
        var now = clock.GetUtcNow();
        if (guest.ExpiresAt > now && PromotionRules.ReplacesAttribution(account, now))
        {
            if (account is not null)
                session.Delete(account);
            await session.StoreAsync(new PromotionAttribution
            {
                Id = accountId,
                Ref = guest.Ref,
                UtmSource = guest.UtmSource,
                UtmMedium = guest.UtmMedium,
                UtmCampaign = guest.UtmCampaign,
                UtmContent = guest.UtmContent,
                AttributedAt = guest.AttributedAt,
                ExpiresAt = guest.ExpiresAt,
            }, accountId, cancellationToken);
        }
        session.Delete(guest);
    }

    /// <summary>The goods of the link's seller on the order (every line for a platform link), active lines only.</summary>
    internal static Money AttributedTotal(CheckoutOrder order, string? organizationId)
        => Money.Sum(order.Lines
            .Where(l => l.Status == OrderLineStatus.Active && (organizationId is null || string.Equals(l.SellerOrganizationId, organizationId, StringComparison.Ordinal)))
            .Select(l => l.Total), order.Currency);

    private static string? Clip(string? value) => value is null ? null : value.Length <= 128 ? value : value[..128];
}

/// <summary>The audit actions Promotions writes (the money audit, default 8).</summary>
public static class PromotionAuditActions
{
    public const string CommissionBooked = "commerce-promotions.commission";
    public const string CommissionReversed = "commerce-promotions.commission-reversed";
    public const string CodeSaved = "commerce-promotions.code-saved";
    public const string SalePriceScheduled = "commerce-promotions.sale-price-scheduled";
}
