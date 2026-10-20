using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Inventory.Jobs;
using MintPlayer.Spark.Commerce.Payments;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>Checkout hears the payments package (§4.5): payment, refund and dispute changes drive the order (§4.4, GB1).</summary>
internal sealed class CheckoutPaymentRecipient(OrderLifecycle lifecycle) : IPaymentEventRecipient
{
    public Task OnPaymentChangedAsync(PaymentChangeNotice notice, CancellationToken cancellationToken)
        => IsOrder(notice.Payment.Reference) ? lifecycle.OnPaymentAsync(notice.Payment, cancellationToken) : Task.CompletedTask;

    public Task OnRefundChangedAsync(RefundChangeNotice notice, CancellationToken cancellationToken)
        => IsOrder(notice.Payment.Reference) ? lifecycle.OnRefundAsync(notice, cancellationToken) : Task.CompletedTask;

    public Task OnDisputeChangedAsync(DisputeChangeNotice notice, CancellationToken cancellationToken)
        => IsOrder(notice.Dispute.Reference) ? lifecycle.OnDisputeAsync(notice, cancellationToken) : Task.CompletedTask;

    internal static bool IsOrder(string? reference) => reference is { } r && r.StartsWith("CheckoutOrders/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The inventory sweeper released a lapsed claim: when it belonged to an order still awaiting payment, the payment
/// window is over, so the provider payment is cancelled actively and the order expires (D3). A basket's lapsed hold
/// needs nothing: the basket shows it lapsed (the hold stopped counting on its own, I3).
/// </summary>
internal sealed class CheckoutClaimLapsed(IDocumentStore store, OrderLifecycle lifecycle) : IInventoryClaimLapsed
{
    public async Task OnLapsedAsync(InventoryClaim claim, CancellationToken cancellationToken)
    {
        if (claim.Id is not { } claimId || !claimId.StartsWith("claims/checkout/", StringComparison.Ordinal))
            return;
        string? orderId;
        using (var session = store.OpenAsyncSession())
        {
            orderId = await session.Query<CheckoutOrder>()
                .Where(o => o.State == OrderState.PaymentPending && o.Lines.Any(l => l.ClaimId == claimId))
                .Select(o => o.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (orderId is null)
            return;
        await lifecycle.UpdateAsync(orderId, (_, order, raised)
            => lifecycle.EndUnpaidAsync(order, OrderState.Expired, "hold lapsed", raised, cancelProvider: true, cancellationToken), cancellationToken);
    }
}

/// <summary>
/// X1: a guest's basket moves onto the account they sign in to, inside the visitor merge's cluster-wide transaction.
/// The baskets are merged when the account's basket could take the guest's lines (same channel, or
/// <c>Basket:MixChannels</c>; same seller, or mixed sellers allowed); otherwise the more recently changed basket wins and
/// the other's holds lapse on their own (I3). An account basket with an order awaiting payment is kept as it is.
/// </summary>
internal sealed class CheckoutBasketMerge(IOptionsMonitor<SparkCommerceOptions> commerce, TimeProvider clock) : ISparkVisitorMergeHandler
{
    public async Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var guest = await session.LoadAsync<CheckoutBasket>(CheckoutBasket.IdForVisitor(visitorId), cancellationToken);
        if (guest is null)
            return;
        // D16: the moved lines keep counting against the guest's visitor limit key (CheckoutService.Identifiers(line)).
        foreach (var line in guest.Lines)
            line.LimitVisitorId ??= visitorId;
        var accountId = CheckoutBasket.IdForUser(userId);
        var account = await session.LoadAsync<CheckoutBasket>(accountId, cancellationToken);

        if (account is null)
        {
            var moved = new CheckoutBasket
            {
                Id = accountId, UserId = userId, Currency = guest.Currency, Lines = guest.Lines, Choices = guest.Choices,
                Email = guest.Email, PendingOrderId = guest.PendingOrderId, UpdatedAt = clock.GetUtcNow(),
            };
            await session.StoreAsync(moved, accountId, cancellationToken);
        }
        else if (account.PendingOrderId is null)
        {
            Merge(guest, account, commerce.CurrentValue);
            account.UpdatedAt = clock.GetUtcNow();
            account.ReminderSentAt = null;
        }
        session.Delete(guest);
    }

    /// <summary>The merge rule, separated for the unit test.</summary>
    internal static void Merge(CheckoutBasket guest, CheckoutBasket account, SparkCommerceOptions settings)
    {
        if (guest.Lines.Count == 0)
            return;
        var all = account.Lines.Concat(guest.Lines).ToList();
        var fits = (settings.Basket.MixChannels || all.Select(l => l.Channel).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1)
            && (CheckoutService.MixedSellersAllowed(settings) || all.Select(l => l.SellerOrganizationId).Distinct().Count() <= 1)
            && (account.Currency is null || guest.Currency is null || account.Currency == guest.Currency);
        if (fits)
        {
            account.Lines.AddRange(guest.Lines);
            account.Currency ??= guest.Currency;
        }
        else if (guest.UpdatedAt >= account.UpdatedAt)
        {
            account.Lines = guest.Lines;
            account.Currency = guest.Currency;
        }
        foreach (var (key, value) in guest.Choices)
            account.Choices[key] = value;
        account.Email ??= guest.Email;
    }
}

/// <summary>GB1: the order's facts for a dispute's evidence pack.</summary>
internal sealed class CheckoutDisputeEvidence(IDocumentStore store) : IDisputeEvidenceContributor
{
    public async Task<DisputeEvidenceSection?> ContributeAsync(DisputeEvidenceRequest request, CancellationToken cancellationToken)
    {
        if (!CheckoutPaymentRecipient.IsOrder(request.OrderReference))
            return null;
        using var session = store.OpenAsyncSession();
        var order = await session.LoadAsync<CheckoutOrder>(request.OrderReference, cancellationToken);
        if (order is null)
            return null;
        return Build(order);
    }

    internal static DisputeEvidenceSection Build(CheckoutOrder order)
    {
        static string At(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"Order {order.Number} placed {At(order.CreatedAt)} ({order.Origin}), total {CheckoutMails.Format(order.Total)}",
        };
        if (order.ConfirmedAt is { } confirmed)
            lines.Add($"Confirmed {At(confirmed)}");
        if (order.BuyerUserId is not null)
            lines.Add("Placed by a signed-in account");
        else if (order.VisitorId is not null)
            lines.Add("Placed by a guest visitor " + order.VisitorId);
        if (order.PurchaseIp is { } ip)
            lines.Add("Purchase IP " + ip);
        foreach (var line in order.Lines)
            lines.Add($"{line.Quantity} × {line.Title?.GetValue("en") ?? line.ItemId} ({line.Status}){(line.Units.Count > 0 ? ", " + string.Join(" ", line.Units) : "")}{(line.ServiceDate is { } s ? ", service date " + At(s) : "")}{(line.ShippedAt is { } sh ? ", shipped " + At(sh) : "")}");
        foreach (var acceptance in order.Acceptances)
            lines.Add($"Accepted {acceptance.Kind} v{acceptance.Version} ({acceptance.DocumentId}) at {At(acceptance.AcceptedAt)}");
        if (order.AgeAttestation is { } age)
            lines.Add($"Attested to be at least {age.MinimumAge} at {At(age.AttestedAt)}");
        foreach (var entry in order.Log)
            lines.Add($"{At(entry.At)} {entry.Event}{(entry.Detail is null ? "" : ": " + entry.Detail)}");

        var fields = new Dictionary<DisputeEvidenceField, string>
        {
            [DisputeEvidenceField.ProductDescription] = string.Join("; ", order.Lines.Select(l => $"{l.Quantity} × {l.Title?.GetValue("en") ?? l.ItemId}")),
        };
        if (order.BuyerEmail is { } email)
            fields[DisputeEvidenceField.CustomerEmail] = email;
        if (order.BuyerName is { } name)
            fields[DisputeEvidenceField.CustomerName] = name;
        if (order.PurchaseIp is { } purchaseIp)
            fields[DisputeEvidenceField.CustomerPurchaseIp] = purchaseIp;
        if (order.Lines.Where(l => l.ServiceDate is not null).Select(l => l.ServiceDate!.Value).DefaultIfEmpty().Min() is { } service && service != default)
            fields[DisputeEvidenceField.ServiceDate] = service.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (order.Acceptances.FirstOrDefault(a => a.Kind == LegalDocumentKinds.TermsOfSale) is { } terms)
            fields[DisputeEvidenceField.RefundPolicyDisclosure] = $"The buyer accepted the terms of sale v{terms.Version} at {At(terms.AcceptedAt)} before paying.";
        return new DisputeEvidenceSection("checkout.order", "Order", lines, fields);
    }
}

/// <summary>GB14: the seller's buyers export (<c>buyers</c>), scoped to the seller's own orders.</summary>
internal sealed class CheckoutBuyersExport(IDocumentStore store) : ISellerExportSource
{
    public string Kind => "buyers";

    public IReadOnlyList<string> Columns { get; } = ["order", "placedAt", "state", "email", "name", "item", "quantity", "total", "currency"];

    public async IAsyncEnumerable<IReadOnlyList<string?>> GetRowsAsync(string organizationId, DateTimeOffset? from, DateTimeOffset? to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var query = session.Query<CheckoutOrder>().Where(o => o.Sellers.Contains(organizationId));
        if (from is { } f)
            query = query.Where(o => o.CreatedAt >= f);
        if (to is { } t)
            query = query.Where(o => o.CreatedAt < t);
        await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
        while (await stream.MoveNextAsync())
        {
            var order = stream.Current.Document;
            foreach (var line in order.Lines.Where(l => l.SellerOrganizationId == organizationId))
                yield return [order.Number, order.CreatedAt.ToString("o", CultureInfo.InvariantCulture), order.State.ToString(), order.BuyerEmail, order.BuyerName,
                    line.Title?.GetValue("en") ?? line.ItemId, line.Quantity.ToString(CultureInfo.InvariantCulture),
                    line.Charged().Amount.ToString(CultureInfo.InvariantCulture), line.Total.Currency];
        }
    }
}
