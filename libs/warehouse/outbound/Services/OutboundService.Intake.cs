using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using MintPlayer.Spark.Warehouse.Stock;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

internal sealed partial class OutboundService
{
    /// <summary>
    /// Intake (PRD §4.3): a new replica becomes an <see cref="OutboundOrder"/>, accepted or rejected with a reason (owner not
    /// served, unknown SKU, invalid address) and routed to a site; a known one takes the shop's new intent (holds, priority,
    /// ship-not-before) and its cancellation request. Accepted units enter the channel's open quantity (§4.4) in the same
    /// session as the line's <see cref="OutboundLine.OpenReported"/>, by a deterministic op id per line.
    /// </summary>
    public async Task<IntakeOutcome> ReceiveAsync(string sourceModule, FulfilmentOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.Id))
            return IntakeOutcome.Ignored;
        var orderId = OutboundOrder.IdFor(order.Id);

        var existing = await LoadOrderAsync(orderId, cancellationToken);
        var outcome = IntakeOutcome.Updated;
        if (existing is null)
        {
            var created = await CreateAsync(sourceModule, order, orderId, cancellationToken);
            if (created is null)
                existing = await LoadOrderAsync(orderId, cancellationToken); // another delivery created it meanwhile
            else
                outcome = created.State == OutboundState.Rejected ? IntakeOutcome.Rejected : IntakeOutcome.Accepted;
        }
        else
        {
            // The shop's intent; lines and address are fixed after acceptance (a change is a cancellation plus a new order).
            await ChangeOrderAsync(orderId, (_, o) =>
            {
                if (o.IsClosed)
                    return Task.FromResult((false, false));
                var changed = !o.Holds.SequenceEqual(order.Holds) || o.Priority != order.Priority || o.ShipNotBefore != order.ShipNotBefore
                    || (order.CancellationRequested && !o.CancellationRequested);
                o.Holds = [.. order.Holds];
                o.Priority = order.Priority;
                o.ShipNotBefore = order.ShipNotBefore;
                if (order.CancellationRequested && !o.CancellationRequested)
                {
                    o.CancellationRequested = true;
                    o.CancellationLineIds = [.. order.CancellationLineIds];
                }
                return Task.FromResult((changed, changed));
            }, cancellationToken);
        }

        var current = await LoadOrderAsync(orderId, cancellationToken);
        if (current is null || current.State == OutboundState.Rejected)
            return outcome == IntakeOutcome.Updated ? IntakeOutcome.Ignored : outcome;

        // Resume an acceptance a crash interrupted: every accepted unit is in the channel's open quantity.
        await ReportOpenAsync(current, cancellationToken);

        if (current.CancellationRequested && current.CancellationAnswer is null)
        {
            await HandleAsync(orderId, cancellationToken);
            return outcome;
        }

        if (Options.Release.Mode == ReleaseMode.Immediate && current.State == OutboundState.Accepted)
            await ReleaseAsync([orderId], cancellationToken: cancellationToken);
        return outcome;
    }

    /// <summary>The new order, accepted or rejected; null when another delivery stored it first.</summary>
    private async Task<OutboundOrder?> CreateAsync(string sourceModule, FulfilmentOrder replica, string orderId, CancellationToken cancellationToken)
    {
        var now = Now;
        var order = new OutboundOrder
        {
            Id = orderId,
            FulfilmentOrderId = replica.Id!,
            SourceModule = sourceModule,
            ExternalOwnerId = replica.OwnerId,
            Channel = replica.Channel,
            OrderNumber = replica.OrderNumber,
            PlacedAt = replica.PlacedAt,
            ShipTo = replica.ShipTo,
            CarrierCode = replica.CarrierCode,
            ServiceCode = replica.ServiceCode,
            ShipNotBefore = replica.ShipNotBefore,
            ShipComplete = replica.ShipComplete,
            BackorderPolicy = replica.BackorderPolicy,
            Priority = replica.Priority,
            Holds = [.. replica.Holds],
            CancellationRequested = replica.CancellationRequested,
            CancellationLineIds = [.. replica.CancellationLineIds],
            State = OutboundState.Accepted,
            UpdatedAt = now,
        };

        var problem = await ValidateAsync(sourceModule, replica, order, cancellationToken);
        if (problem is { } rejected)
        {
            order.State = OutboundState.Rejected;
            order.RejectionReason = rejected.Reason;
            order.RejectionDetail = rejected.Detail;
        }
        else
        {
            order.AcceptedAt = now;
        }

        using var session = OutboundSessions.Open(store);
        // A redelivery, a reconcile and a push may race: the first store wins, the others conflict and read it.
        await session.StoreAsync(order, string.Empty, orderId, cancellationToken);
        await OutboundStatus.WriteAsync(session, order, publisher, now, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return null;
        }
        logger.LogInformation("Outbound order {OrderId} {State}{Reason}.", orderId, order.State,
            order.RejectionReason is { } reason ? $" ({reason}: {order.RejectionDetail})" : "");
        return order;
    }

    /// <summary>Owner link, lines and SKUs, address, site — or the reason to reject. Fills the order's owner, items and site.</summary>
    private async Task<(FulfilmentRejectionReason Reason, string Detail)?> ValidateAsync(string sourceModule, FulfilmentOrder replica, OutboundOrder order, CancellationToken cancellationToken)
    {
        var link = await links.ResolveAsync(sourceModule, replica.OwnerId, cancellationToken);
        if (link is null)
            return (FulfilmentRejectionReason.OwnerNotServed, replica.OwnerId);
        order.OrganizationId = link.OrganizationId;

        if (replica.Lines.Count == 0)
            return (FulfilmentRejectionReason.Other, "The order has no lines.");
        if (replica.Lines.Select(l => l.LineId).Distinct(StringComparer.Ordinal).Count() != replica.Lines.Count || replica.Lines.Any(l => string.IsNullOrWhiteSpace(l.LineId)))
            return (FulfilmentRejectionReason.Other, "Line ids are missing or not unique.");

        var number = 0;
        foreach (var line in replica.Lines)
        {
            if (line.Quantity <= 0)
                return (FulfilmentRejectionReason.Other, $"Line {line.LineId} has no positive quantity.");
            var item = await items.FindAsync(link.OrganizationId, line.Sku, cancellationToken);
            if (item is null || !item.Active)
                return (FulfilmentRejectionReason.UnknownSku, line.Sku);
            order.Lines.Add(new OutboundLine
            {
                LineId = line.LineId,
                Number = ++number,
                Sku = line.Sku,
                Name = line.Name,
                ItemId = item.Id!,
                Quantity = line.Quantity,
                RequiredLotCode = string.IsNullOrWhiteSpace(line.RequiredLotCode) ? null : line.RequiredLotCode.Trim(),
            });
        }

        if (AddressProblem(replica.ShipTo) is { } addressProblem)
            return (FulfilmentRejectionReason.InvalidAddress, addressProblem);

        var site = await RouteAsync(replica, link, cancellationToken);
        if (site.SiteId is null)
            return (FulfilmentRejectionReason.Other, site.Reason);
        order.SiteId = site.SiteId;
        order.RoutingReason = site.Reason;
        return null;
    }

    /// <summary>The shape a carrier label needs (the reference data's validation runs in the shop, before it publishes).</summary>
    internal static string? AddressProblem(FulfilmentAddress? address)
    {
        if (address is null)
            return "ShipTo";
        if (string.IsNullOrWhiteSpace(address.Name) && string.IsNullOrWhiteSpace(address.Company))
            return nameof(FulfilmentAddress.Name);
        if (string.IsNullOrWhiteSpace(address.Line1))
            return nameof(FulfilmentAddress.Line1);
        if (string.IsNullOrWhiteSpace(address.City))
            return nameof(FulfilmentAddress.City);
        if (address.CountryCode is not { Length: 2 } country || !country.All(char.IsAsciiLetterUpper))
            return nameof(FulfilmentAddress.CountryCode);
        if (address.PostalCode is { Length: > 16 })
            return nameof(FulfilmentAddress.PostalCode);
        return null;
    }

    /// <summary>Site routing (WD3): the routers in order (the configuration rules at 100), else the owner link's default site.</summary>
    private async Task<SiteRoutingDecision> RouteAsync(FulfilmentOrder replica, WarehouseOwnerLink link, CancellationToken cancellationToken)
    {
        List<string> candidates;
        using (var session = store.OpenAsyncSession())
        {
            candidates = (await session.Advanced.LoadStartingWithAsync<WarehouseSite>(WarehouseSite.IdPrefix, pageSize: 1024, token: cancellationToken))
                .Where(s => s.Active).Select(s => s.Id!).ToList();
        }
        var request = new SiteRoutingRequest(replica.Id!, link.OrganizationId, replica.Channel, replica.ShipTo?.CountryCode,
            replica.Lines.Select(l => new SiteRoutingLine(l.Sku, l.Quantity)).ToList(), candidates);
        foreach (var router in siteRouters.OrderBy(r => r.Order))
        {
            if (await router.RouteAsync(request, cancellationToken) is { } decision)
            {
                if (decision.SiteId is not null && !candidates.Contains(decision.SiteId, StringComparer.OrdinalIgnoreCase))
                    return SiteRoutingDecision.Reject($"Routing chose {decision.SiteId}, which is not an active site.");
                return decision;
            }
        }
        if (link.DefaultSiteId is { } defaultSite && candidates.Contains(defaultSite, StringComparer.OrdinalIgnoreCase))
            return SiteRoutingDecision.To(defaultSite, "owner-default");
        if (candidates.Count == 1)
            return SiteRoutingDecision.To(candidates[0], "only-site");
        return SiteRoutingDecision.Reject("No routing rule matched and the owner has no default site.");
    }

    /// <summary>Raises the channel's open quantity by each line's accepted units not reported yet (§4.4).</summary>
    private async Task ReportOpenAsync(OutboundOrder order, CancellationToken cancellationToken)
    {
        if (order.OrganizationId is null || order.State is OutboundState.Rejected or OutboundState.Cancelled)
            return;
        foreach (var line in order.Lines.Where(l => l.OpenReported == 0 && l.Quantity > l.Shipped + l.Cancelled))
        {
            var delta = line.Quantity - line.Shipped - line.Cancelled;
            var op = Op($"{order.Id}/open/{line.LineId}", reference: order.Id) with
            {
                InSession = WithOrder(order.Id!, (_, o) =>
                {
                    var l = o.Line(line.LineId);
                    if (l is null || l.OpenReported != 0)
                        return Task.FromResult<string?>("Reported already.");
                    l.OpenReported = delta;
                    return Task.FromResult<string?>(null);
                }),
            };
            var result = await stock.ChangeOpenAsync(new ChangeOpenQuantity(op, order.OrganizationId, line.ItemId, order.Channel, delta), cancellationToken);
            if (!result.Succeeded && result.Refusal != StockRefusal.Declined)
                logger.LogWarning("Open quantity of {OrderId} line {LineId} not reported: {Refusal} {Detail}", order.Id, line.LineId, result.Refusal, result.Detail);
        }
    }

    /// <summary>Lowers the channel's open quantity and records the cancelled units, in one session (op id: the cumulative target, so a retry is applied once).</summary>
    private async Task<bool> CancelUnitsAsync(string orderId, string lineId, decimal units, bool remainder, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        var line = order?.Line(lineId);
        if (order is null || line is null || units <= 0 || order.OrganizationId is null)
            return false;
        var target = line.Cancelled + units;
        var openDelta = Math.Min(units, line.OpenReported);
        var op = Op($"{orderId}/cancel/{lineId}/{target}", reference: orderId) with
        {
            InSession = WithOrder(orderId, (_, o) =>
            {
                var l = o.Line(lineId);
                if (l is null || l.Cancelled + units != target)
                    return Task.FromResult<string?>("The line changed meanwhile.");
                l.Cancelled = target;
                l.OpenReported -= openDelta;
                if (remainder)
                    l.Backordered = Math.Max(0, l.Backordered - units);
                Settle(o);
                return Task.FromResult<string?>(null);
            }),
        };
        if (openDelta > 0)
            return (await stock.ChangeOpenAsync(new ChangeOpenQuantity(op, order.OrganizationId, line.ItemId, order.Channel, -openDelta), cancellationToken)).Succeeded;

        // Nothing counted in the open quantity (a crashed acceptance): an order-only change.
        return await ChangeOrderAsync(orderId, (_, o) =>
        {
            var l = o.Line(lineId);
            if (l is null || l.Cancelled + units != target)
                return Task.FromResult((false, false));
            l.Cancelled = target;
            if (remainder)
                l.Backordered = Math.Max(0, l.Backordered - units);
            Settle(o);
            return Task.FromResult((true, true));
        }, cancellationToken);
    }
}

/// <summary>Consumes the shops' <c>fulfilment-orders</c> replicas (PRD §4.3, WD2: each side reacts to the other's replicas).</summary>
internal sealed partial class FulfilmentOrderConsumer : IReplicaChanged<FulfilmentOrder>
{
    [Inject] private readonly IOutboundIntake intake;

    public async Task OnReplicaChangedAsync(ReplicaChange<FulfilmentOrder> change, CancellationToken cancellationToken)
    {
        // A shop never deletes an order (cancellation is a status); a deleted replica changes nothing here.
        if (change.Current is { } current)
            await intake.ReceiveAsync(change.SourceModule, current, cancellationToken);
    }
}

/// <summary>The configuration router (PRD §4.13 <c>Spark:Warehouse:Routing</c>): (owner, channel, ship-to country) → site.</summary>
internal sealed class ConfigurationSiteRouter(Microsoft.Extensions.Options.IOptions<SparkWarehouseOutboundOptions> options) : ISiteRouter
{
    public int Order => 100;

    public ValueTask<SiteRoutingDecision?> RouteAsync(SiteRoutingRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var rule in options.Value.Routing.Rules)
        {
            if (rule.Channel is not null && !string.Equals(rule.Channel, request.Channel, StringComparison.OrdinalIgnoreCase))
                continue;
            if (rule.Country is not null && !string.Equals(rule.Country, request.ShipToCountry, StringComparison.OrdinalIgnoreCase))
                continue;
            if (rule.Owner is not null && !string.Equals(rule.Owner, request.OwnerId, StringComparison.Ordinal))
                continue;
            return ValueTask.FromResult<SiteRoutingDecision?>(SiteRoutingDecision.To(WarehouseSite.IdFor(rule.Site), $"rule:{rule.Site}"));
        }
        return ValueTask.FromResult<SiteRoutingDecision?>(null);
    }
}
