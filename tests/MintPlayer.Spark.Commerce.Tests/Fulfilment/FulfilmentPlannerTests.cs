using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Fulfilment.Services;
using MintPlayer.Spark.Fulfilment.Contracts;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Fulfilment;

/// <summary>
/// The shop's fulfilment decisions (warehouse §4.3, §4.4 rule 1), derived from state only: cancellation is a status, an edit
/// is a cancellation plus a new order, and refunded units go back on sale only once they can no longer ship.
/// </summary>
public class FulfilmentPlannerTests
{
    private const string Address = "addr";

    private static FulfilmentRevision Revision(int n, params (string LineId, int Quantity)[] lines) => new()
    {
        Id = "FulfilmentOrders/shop-a/SO-1/platform/" + n,
        Number = n,
        Lines = lines.Select(l => new FulfilmentLinkLine { LineId = l.LineId, Quantity = l.Quantity }).ToList(),
        PublishedAt = Now,
    };

    private static FulfilmentOrder Published(FulfilmentRevision revision, string addressName = "A. Buyer") => new()
    {
        Id = revision.Id,
        ShipTo = new FulfilmentAddress { Name = addressName },
        Lines = revision.Lines.Select(l => new FulfilmentOrderLine { LineId = l.LineId, Quantity = l.Quantity }).ToList(),
    };

    private static FulfilmentOrderStatus Status(FulfilmentRevision revision, FulfilmentOrderState state, params (string LineId, decimal Shipped, decimal Cancelled)[] lines) => new()
    {
        FulfilmentOrderId = revision.Id,
        State = state,
        Lines = lines.Select(l => new FulfilmentOrderLineStatus { LineId = l.LineId, Shipped = l.Shipped, Cancelled = l.Cancelled }).ToList(),
    };

    private static RevisionView View(FulfilmentRevision revision, FulfilmentOrderStatus? status = null, string addressName = "A. Buyer")
        => new(revision, Published(revision, addressName), status, []);

    private static string Key(string name = "A. Buyer") => FulfilmentPlanner.AddressKey(new FulfilmentAddress { Name = name });

    [Fact]
    public void A_new_order_publishes_every_line_still_wanted()
    {
        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 2, 0, false), new PlannedLine("L2", 1, 1, false), new PlannedLine("L3", 4, 0, Gone: true)], [], Key(), Now);
        plan.NewRevision.Select(l => (l.LineId, l.Quantity)).Should().Equal(("L1", 2));
        plan.CancelRevisionIds.Should().BeEmpty();
        plan.Waiting.Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_published_without_an_address_and_staff_are_told()
    {
        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 2, 0, false)], [], addressKey: null, Now);
        plan.NewRevision.Should().BeEmpty();
        plan.Attention!.Kind.Should().Be(FulfilmentAttentionKind.MissingAddress);
    }

    [Fact]
    public void An_order_in_line_with_the_warehouse_changes_nothing()
    {
        var revision = Revision(1, ("L1", 2));
        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 2, 0, false)], [View(revision, Status(revision, FulfilmentOrderState.Picking))], Key(), Now);
        plan.NewRevision.Should().BeEmpty();
        plan.CancelRevisionIds.Should().BeEmpty();
        plan.Settled.Should().BeFalse(); // two units may still ship
    }

    [Fact]
    public void A_refund_before_shipment_cancels_the_whole_order_and_keeps_the_units_sold_until_the_answer()
    {
        var revision = Revision(1, ("L1", 2));
        var lines = new[] { new PlannedLine("L1", 2, RefundedBeforeShipment: 1, Gone: false) };

        var plan = FulfilmentPlanner.Plan(lines, [View(revision, Status(revision, FulfilmentOrderState.Accepted))], Key(), Now);
        plan.CancelRevisionIds.Should().Equal(revision.Id);
        plan.NewRevision.Should().BeEmpty();
        plan.Lines["L1"].Restockable.Should().Be(0); // rule 1: not on the request

        // Asked and unanswered: wait, still nothing back on sale.
        revision.CancellationRequestedAt = Now;
        var waiting = FulfilmentPlanner.Plan(lines, [View(revision, Status(revision, FulfilmentOrderState.Accepted))], Key(), Now);
        waiting.Waiting.Should().BeTrue();
        waiting.CancelRevisionIds.Should().BeEmpty();
        waiting.Lines["L1"].Restockable.Should().Be(0);

        // Accepted: the refunded unit goes back on sale and the rest is ordered again (an edit = cancellation + new order).
        var accepted = Status(revision, FulfilmentOrderState.Cancelled, ("L1", 0, 2));
        accepted.Cancellation = FulfilmentCancellationAnswer.Accepted;
        var after = FulfilmentPlanner.Plan(lines, [View(revision, accepted)], Key(), Now);
        after.Waiting.Should().BeFalse();
        after.Lines["L1"].Restockable.Should().Be(1);
        after.NewRevision.Select(l => (l.LineId, l.Quantity)).Should().Equal(("L1", 1));
    }

    [Fact]
    public void A_cancellation_accepted_after_a_partial_shipment_orders_only_what_is_still_needed()
    {
        var revision = Revision(1, ("L1", 3));
        revision.CancellationRequestedAt = Now;
        var status = Status(revision, FulfilmentOrderState.Shipped, ("L1", 1, 2));
        status.Cancellation = FulfilmentCancellationAnswer.Accepted;

        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 3, 1, false)], [View(revision, status)], Key(), Now);
        plan.Lines["L1"].Shipped.Should().Be(1);
        plan.Lines["L1"].Needed.Should().Be(1);
        plan.NewRevision.Single().Quantity.Should().Be(1);
        plan.Lines["L1"].Restockable.Should().Be(1);

        // With the replacement out, the refunded unit stays restockable (the new order carries only what is needed).
        var replacement = Revision(2, ("L1", 1));
        var next = FulfilmentPlanner.Plan([new PlannedLine("L1", 3, 1, false)], [View(revision, status), View(replacement)], Key(), Now);
        next.NewRevision.Should().BeEmpty();
        next.CancelRevisionIds.Should().BeEmpty();
        next.Lines["L1"].Restockable.Should().Be(1);
    }

    [Fact]
    public void A_refused_cancellation_is_never_asked_again_and_a_unit_that_shipped_anyway_stays_sold()
    {
        var revision = Revision(1, ("L1", 1));
        revision.CancellationRequestedAt = Now;
        var status = Status(revision, FulfilmentOrderState.Shipped, ("L1", 1, 0));
        status.Cancellation = FulfilmentCancellationAnswer.Refused;
        status.CancellationRefusalReason = "already picked up by the courier";

        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 1, RefundedBeforeShipment: 1, Gone: false)], [View(revision, status)], Key(), Now);
        plan.CancelRevisionIds.Should().BeEmpty();
        plan.NewRevision.Should().BeEmpty();
        plan.Lines["L1"].Restockable.Should().Be(0); // shipped: the warehouse's ShippedTotal counts it
        plan.Attention!.Kind.Should().Be(FulfilmentAttentionKind.CancellationRefused);
        plan.Attention.Detail.Should().Be("already picked up by the courier");
    }

    [Fact]
    public void A_rejected_order_blocks_a_new_one_until_staff_acknowledge_it()
    {
        var revision = Revision(1, ("L1", 2));
        var status = Status(revision, FulfilmentOrderState.Rejected);
        status.RejectionReason = FulfilmentRejectionReason.UnknownSku;
        status.RejectionDetail = "OIL-75";
        var lines = new[] { new PlannedLine("L1", 2, 0, false) };

        var plan = FulfilmentPlanner.Plan(lines, [View(revision, status)], Key(), Now);
        plan.NewRevision.Should().BeEmpty();
        plan.Attention!.Kind.Should().Be(FulfilmentAttentionKind.Rejected);
        plan.Attention.Detail.Should().Be("UnknownSku: OIL-75");

        revision.Acknowledged = true;
        var retried = FulfilmentPlanner.Plan(lines, [View(revision, status)], Key(), Now);
        retried.NewRevision.Single().Quantity.Should().Be(2);
        retried.Attention.Should().BeNull();
    }

    [Fact]
    public void A_cancelled_remainder_is_not_ordered_again_and_comes_back_on_sale_once_refunded()
    {
        var revision = Revision(1, ("L1", 3));
        var status = Status(revision, FulfilmentOrderState.Shipped, ("L1", 2, 1)); // CancelRemainder, nobody asked

        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 3, 0, false)], [View(revision, status)], Key(), Now);
        plan.NewRevision.Should().BeEmpty();
        plan.Lines["L1"].Undeliverable.Should().Be(1);
        plan.Attention!.Kind.Should().Be(FulfilmentAttentionKind.Undeliverable);
        plan.Settled.Should().BeTrue();

        // Staff refunded it: it can never ship, so its unit goes back on sale.
        var refunded = FulfilmentPlanner.Plan([new PlannedLine("L1", 3, 1, false)], [View(revision, status)], Key(), Now);
        refunded.Lines["L1"].Undeliverable.Should().Be(0);
        refunded.Lines["L1"].Restockable.Should().Be(1);
        refunded.NewRevision.Should().BeEmpty();
        refunded.Attention.Should().BeNull();
    }

    [Fact]
    public void An_address_change_cancels_the_live_order_and_republishes_once_accepted()
    {
        var revision = Revision(1, ("L1", 2));
        var lines = new[] { new PlannedLine("L1", 2, 0, false) };
        var moved = Key("New Name");

        var plan = FulfilmentPlanner.Plan(lines, [View(revision, Status(revision, FulfilmentOrderState.Accepted))], moved, Now);
        plan.CancelRevisionIds.Should().Equal(revision.Id);

        revision.CancellationRequestedAt = Now;
        var accepted = Status(revision, FulfilmentOrderState.Cancelled, ("L1", 0, 2));
        accepted.Cancellation = FulfilmentCancellationAnswer.Accepted;
        var after = FulfilmentPlanner.Plan(lines, [View(revision, accepted)], moved, Now);
        after.NewRevision.Single().Quantity.Should().Be(2);
        after.Lines["L1"].Restockable.Should().Be(0); // nothing was refunded
    }

    [Fact]
    public void Statuses_and_shipments_count_whichever_is_ahead_and_a_late_packed_changes_nothing()
    {
        var revision = Revision(1, ("L1", 2));
        var shipment = new FulfilmentShipment { FulfilmentOrderId = revision.Id, Lines = [new FulfilmentShipmentLine { LineId = "L1", Quantity = 2 }] };
        // The shipment arrived, the status still says Packed (reordered delivery).
        var view = new RevisionView(revision, Published(revision), Status(revision, FulfilmentOrderState.Packed), [shipment]);

        var plan = FulfilmentPlanner.Plan([new PlannedLine("L1", 2, 0, false)], [view], Key(), Now);
        plan.Lines["L1"].Shipped.Should().Be(2);
        plan.Lines["L1"].AtRisk.Should().Be(0);
        plan.Settled.Should().BeTrue();
    }
}
