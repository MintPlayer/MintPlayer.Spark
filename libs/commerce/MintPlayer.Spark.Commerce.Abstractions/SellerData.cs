namespace MintPlayer.Spark.Commerce;

/// <summary>
/// A source of buyer or attendee rows for a seller's own export (GB14, §4.15). In <see cref="MerchantMode.ConnectedSellers"/>
/// mode the seller is the controller of its buyers' data for its own sales; Commerce serves the export (CSV), checks
/// the caller holds <c>finance</c> or the export role in the seller organization, and audits every export. Checkout
/// registers the buyers source, Tickets the attendees source.
/// </summary>
public interface ISellerExportSource
{
    /// <summary>The export kind in the URL (<c>buyers</c>, <c>attendees</c>). Permanent.</summary>
    string Kind { get; }

    /// <summary>The column headers, in order.</summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>The rows of <paramref name="organizationId"/>'s own sales only, each with <see cref="Columns"/>' count
    /// of values. The source scopes by seller itself; Commerce never widens it.</summary>
    IAsyncEnumerable<IReadOnlyList<string?>> GetRowsAsync(string organizationId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default);
}

/// <summary>
/// One main ranking parameter of a storefront list or search (GA5): CRD Art. 6a(1)(a) as inserted by Directive (EU)
/// 2019/2161, and P2B Reg. (EU) 2019/1150 Art. 5. Commerce generates the public ranking-parameters page from every
/// registered source, so the page always matches the configured sort rules.
/// </summary>
/// <param name="List">The list or search it applies to (<c>shop.products</c>, <c>ticketing.events</c>).</param>
/// <param name="Parameter">The parameter's translation key (<c>commerce.ranking.relevance</c>).</param>
/// <param name="DescriptionKey">The translation key of its explanation, including its relative importance.</param>
/// <param name="Order">The position of the parameter in the list's ranking (1 = most important).</param>
/// <param name="PaidPlacement">Whether a seller can pay to influence it (it must then be stated, CRD Art. 6a).</param>
public sealed record RankingParameter(string List, string Parameter, string DescriptionKey, int Order, bool PaidPlacement = false);

/// <summary>
/// Resolves the target of a DSA notice (GA5, Art. 16): which seller is responsible for a listing, an event or a review,
/// and whether the target exists. Registered by each package that owns reportable content (Shop, Ticketing.Events,
/// reviews). Without a resolver for a kind, notices of that kind are refused.
/// </summary>
public interface IDsaNoticeTargetResolver
{
    /// <summary>The target kinds this resolver knows (<c>listing</c>, <c>event</c>, <c>review</c>).</summary>
    IReadOnlyCollection<string> Kinds { get; }

    /// <summary>The target, or null when it does not exist (or is not public).</summary>
    Task<DsaNoticeTarget?> ResolveAsync(string kind, string targetId, CancellationToken cancellationToken = default);
}

/// <summary>A resolved notice target.</summary>
/// <param name="TargetId">The document id.</param>
/// <param name="OrganizationId">The responsible seller; null for platform content.</param>
/// <param name="Title">A short label for reviewers.</param>
/// <param name="ModerationObjectTypeId">The persistent-object type id when the target is <c>IModeratable</c>: a
/// signed-in reporter's notice is then also flagged in Moderation's review queue.</param>
public sealed record DsaNoticeTarget(string TargetId, string? OrganizationId, string Title, Guid? ModerationObjectTypeId = null);

/// <summary>Contributes ranking parameters (GA5). Registered by each storefront package (Shop, Ticketing).</summary>
public interface IRankingParameterSource
{
    /// <summary>The parameters, as configured now.</summary>
    IEnumerable<RankingParameter> GetParameters();
}
