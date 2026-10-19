using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Actions;

/// <summary>Venues are a shared catalogue (D8): every signed-in user lists them (the library's rights).</summary>
public partial class VenueActions : DefaultPersistentObjectActions<Venue>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>Venues</c>.</summary>
    public IQueryable<Venue> Venues() => session.Query<Venue>();
}

/// <summary>
/// Layouts: everyone signed in sees the published ones; a draft only its owner; the approvers
/// (<c>ApproveProposal/VenueLayout</c>) every one. Another caller gets the missing-row 404 (#453).
/// </summary>
public partial class VenueLayoutActions : DefaultPersistentObjectActions<VenueLayout>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly VenueAccess access;

    /// <summary>The query <c>VenueLayouts</c>, under the row rule.</summary>
    public IQueryable<VenueLayout> VenueLayouts() => session.Query<VenueLayout>();

    public override async Task<Expression<Func<VenueLayout, bool>>?> GetRowFilterAsync(string action)
    {
        if (await access.IsApproverAsync())
            return null;
        var me = access.CallerId ?? "";
        return l => l.PublishedVersion > 0 || l.OwnerId == me;
    }
}
