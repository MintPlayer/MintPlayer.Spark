using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Shipping.CustomActions;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shipping.Actions;

/// <summary>
/// Shipments are written by <see cref="ISparkShipping"/> only (only QueryRead and the custom actions are granted) and
/// read by the <c>commerce-shipping:staff</c> slot under the <see cref="IShipmentRowFilter"/>s. Another caller gets
/// the missing-row 404 (#453). Each custom action is offered only where it would succeed.
/// </summary>
public partial class ShipmentActions : DefaultPersistentObjectActions<Shipment>
{
    [Inject] private readonly ShipmentAccess access;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ShippingCarrierRegistry registry;

    /// <summary>The query <c>Shipments</c>, newest first, under the row filters.</summary>
    public IQueryable<Shipment> Shipments() => session.Query<Shipment>();

    public override Task<Expression<Func<Shipment, bool>>?> GetRowFilterAsync(string action)
        => access.RowFilterAsync(CancellationToken.None);

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is not Shipment shipment)
            return Task.CompletedTask;
        var capabilities = registry.Find(shipment.Carrier)?.Capabilities;
        var live = shipment.Parcels.Where(p => !p.Voided).ToList();
        var notLeft = live.Count > 0 && live.All(p => p.Status is TrackingStatus.Labelled or TrackingStatus.Pending);

        if (capabilities is not { VoidLabel: true } || !notLeft)
            target.DisableActions(ShipmentActionNames.VoidLabel);
        if (capabilities is not { ReturnLabels: true } || shipment.Direction != ShipmentDirection.Outbound || shipment.Status == TrackingStatus.Cancelled)
            target.DisableActions(ShipmentActionNames.CreateReturnLabel);
        if (!notLeft || shipment.Customs is not null || shipment.HasPendingBooking)
            target.DisableActions(ShipmentActionNames.AddParcel);
        if (!shipment.PendingBookings.Any(b => b.State == PendingBookingState.InDoubt))
            target.DisableActions(ShipmentActionNames.ReleaseBooking);
        return Task.CompletedTask;
    }
}
