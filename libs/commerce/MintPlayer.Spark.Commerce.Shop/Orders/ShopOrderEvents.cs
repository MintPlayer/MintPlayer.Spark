using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shop.Orders;

/// <summary>
/// Checkout's order events for the shop (an <see cref="IOrderEventRecipient"/>): <c>Placed</c> creates the shop's side of
/// each seller's goods with the delivery chosen; <c>Confirmed</c> makes it Paid; a cancelled, expired or voided order
/// cancels it; refused and early-returned lines are never shipped; <c>ReturnExpected</c> records the return and, in
/// ShopOnly mode, books and mails the return label (GB5). Idempotent: Checkout may deliver an event again.
/// </summary>
internal sealed class ShopOrderEvents(ShopOrders orders) : IOrderEventRecipient
{
    public async Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        var order = orderEvent.Order;
        if (order.Id is null || !order.Lines.Any(l => Catalog.ShopItemIds.Handles(l.ItemId)))
            return;
        switch (orderEvent.Kind)
        {
            case OrderEventKind.Placed or OrderEventKind.Confirmed:
                await orders.EnsureAsync(order, cancellationToken);
                await orders.ApplyOrderStateAsync(order, orderEvent.Kind, cancellationToken);
                break;
            case OrderEventKind.Cancelled or OrderEventKind.Expired or OrderEventKind.Voided or OrderEventKind.LineRefused
                or OrderEventKind.RefundIssued or OrderEventKind.WithdrawalRequested:
                await orders.ApplyOrderStateAsync(order, orderEvent.Kind, cancellationToken);
                break;
            case OrderEventKind.ReturnExpected when orderEvent.Return is { } expected:
                await orders.OnReturnExpectedAsync(order, expected, cancellationToken);
                break;
        }
    }
}

/// <summary>
/// Shipping's events for the shop (an <see cref="IShipmentEventRecipient"/>): an outbound shipment delivered tells
/// Checkout per line (the withdrawal and guarantee periods start, GA1/GA8); a return shipment's first scan is the
/// buyer's proof of dispatch and its delivery the receipt (CRD Art. 13(3)). Only shipments booked for a shop order
/// (<c>Reference</c> = the shop order's id) are handled.
/// </summary>
internal sealed class ShopShipmentEvents(IDocumentStore store, ShopOrders orders, TimeProvider clock) : IShipmentEventRecipient
{
    public async Task OnTrackingChangedAsync(ShipmentTrackingNotice notice, CancellationToken cancellationToken)
    {
        var shipment = notice.Shipment;
        if (!shipment.Reference.StartsWith("ShopOrders/", StringComparison.Ordinal))
            return;
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<ShopOrder>(shipment.Reference, cancellationToken) is not { } shopOrder)
            return;
        if (shipment.Direction == ShipmentDirection.Return)
            await orders.OnReturnTrackingAsync(shopOrder, shipment, notice.ProofOfDispatch, cancellationToken);
        else if (shipment.Status == TrackingStatus.Delivered)
            await orders.ApplyDeliveredAsync(shopOrder, shipment.Id!, ShopShipping.EventTime(shipment, s => s == TrackingStatus.Delivered) ?? clock.GetUtcNow(), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The return-label mail (GB5): the label as an attachment, the reason and who pays the return shipping.</summary>
internal sealed class ShopReturnMails(
    IServiceProvider services,
    IDocumentStore store,
    IOptionsMonitor<SparkCommerceShopOptions> options,
    ILogger<ShopReturnMails> logger)
{
    /// <summary>Mails the label of <paramref name="label"/> to the buyer; false without MailManager or a recipient.</summary>
    public async Task<bool> SendReturnLabelAsync(ShopOrder shopOrder, ShopOrderReturn ret, Shipment label, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer)
        {
            logger.LogWarning("No MailManager: the return label of return {ReturnId} is not mailed.", ret.ReturnId);
            return false;
        }
        using var session = store.OpenAsyncSession();
        var order = await session.LoadAsync<CheckoutOrder>(shopOrder.OrderId, cancellationToken);
        if (order is null)
            return false;
        var email = order.BuyerEmail;
        var name = order.BuyerName;
        string? culture = null;
        if (order.BuyerUserId is { } userId && await session.LoadAsync<SparkUser>(userId, cancellationToken) is { } user)
        {
            email ??= user.Email;
            name ??= user.UserName;
            culture = user.PreferredCulture;
        }
        email ??= shopOrder.ShipTo?.Email;
        if (string.IsNullOrWhiteSpace(email))
            return false;
        var attachments = label.Parcels.Where(p => !p.Voided && p.LabelAttachment is { Length: > 0 })
            .Select(p => new SparkMailAttachment
            {
                FileName = $"return-label-{p.Number}.{(p.LabelMediaType == "application/pdf" ? "pdf" : "bin")}",
                ContentType = p.LabelMediaType ?? "application/octet-stream",
                DocumentId = label.Id,
                AttachmentName = p.LabelAttachment,
            }).ToList();
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = options.CurrentValue.Returns.ReturnLabelMailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Attachments = attachments,
            Data = new
            {
                number = shopOrder.OrderNumber,
                buyer_name = name,
                reason = ret.Reason.ToString(),
                seller_pays_return = ret.SellerPaysReturnShipping,
                carrier = label.Carrier,
                tracking_number = label.TrackingNumber,
            },
            DeduplicationKey = $"shop:return-label:{shopOrder.OrderId}:{ret.ReturnId}",
        }, cancellationToken);
        return true;
    }
}
