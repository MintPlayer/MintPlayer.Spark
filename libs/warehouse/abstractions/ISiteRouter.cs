namespace MintPlayer.Spark.Warehouse;

/// <summary>One line of an incoming fulfilment order, as site routing sees it.</summary>
/// <param name="Quantity">In base units.</param>
public sealed record SiteRoutingLine(string Sku, decimal Quantity);

/// <summary>An incoming fulfilment order to route to a site (PRD §4.3 Intake, WD3).</summary>
/// <param name="FulfilmentOrderId">The replica id of the <c>fulfilment-orders</c> contract.</param>
/// <param name="Channel">The shop's sales channel, or null.</param>
/// <param name="ShipToCountry">The ISO 3166-1 alpha-2 code of the ship-to address, or null.</param>
/// <param name="CandidateSiteIds">The sites that serve this owner and are active.</param>
public sealed record SiteRoutingRequest(
    string FulfilmentOrderId,
    string OwnerId,
    string? Channel,
    string? ShipToCountry,
    IReadOnlyList<SiteRoutingLine> Lines,
    IReadOnlyList<string> CandidateSiteIds);

/// <summary>A router's answer.</summary>
/// <param name="SiteId">The chosen site; null means "reject the order" with <paramref name="Reason"/>.</param>
/// <param name="Reason">A short, untranslated reason, recorded on the intake and sent back in a rejection.</param>
public sealed record SiteRoutingDecision(string? SiteId, string Reason)
{
    public static SiteRoutingDecision To(string siteId, string reason) => new(siteId, reason);

    public static SiteRoutingDecision Reject(string reason) => new(null, reason);
}

/// <summary>
/// Chooses the site that fulfils an incoming order (PRD §4.3 Intake: "routed to a site by configuration rules, or to the
/// owner's default site", WD3; configuration <c>Spark:Warehouse:Routing</c>, §4.13). Routers run in <see cref="Order"/>;
/// the first that answers wins. The built-in configuration router (owner, channel, ship-to country → site) runs at 100;
/// when none answers, the owner's default site is used. v1 has no split across sites and no inter-site transfers (WD3).
/// </summary>
public interface ISiteRouter
{
    /// <summary>The position in the chain; lower runs first.</summary>
    int Order { get; }

    /// <summary>A decision, or null to let the next router decide.</summary>
    ValueTask<SiteRoutingDecision?> RouteAsync(SiteRoutingRequest request, CancellationToken cancellationToken = default);
}
