using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Promotions.Actions;

// Every promotions type is written by the library only (only QueryRead is granted; the admin endpoints write) and read
// under PromotionsAccess's row rule: the seller's Owners, Admins and 'promotions' members, and platform admins every
// row. Another caller gets the missing-row 404 (#453).

public partial class PromotionCodeActions : DefaultPersistentObjectActions<PromotionCode>
{
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>PromotionCodes</c>, under the row rule.</summary>
    public IQueryable<PromotionCode> PromotionCodes() => session.Query<PromotionCode>();

    public override async Task<Expression<Func<PromotionCode, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<PromotionCode>(c => c.OrganizationId);
}

public partial class PromotionSalePriceActions : DefaultPersistentObjectActions<PromotionSalePrice>
{
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>PromotionSalePrices</c>, under the row rule.</summary>
    public IQueryable<PromotionSalePrice> PromotionSalePrices() => session.Query<PromotionSalePrice>();

    public override async Task<Expression<Func<PromotionSalePrice, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<PromotionSalePrice>(s => s.OrganizationId);
}

public partial class PromotionWaitingListActions : DefaultPersistentObjectActions<PromotionWaitingList>
{
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>PromotionWaitingLists</c>, under the row rule.</summary>
    public IQueryable<PromotionWaitingList> PromotionWaitingLists() => session.Query<PromotionWaitingList>();

    public override async Task<Expression<Func<PromotionWaitingList, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<PromotionWaitingList>(l => l.OrganizationId);
}

public partial class PromotionTrackingLinkActions : DefaultPersistentObjectActions<PromotionTrackingLink>
{
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>PromotionTrackingLinks</c>, under the row rule.</summary>
    public IQueryable<PromotionTrackingLink> PromotionTrackingLinks() => session.Query<PromotionTrackingLink>();

    public override async Task<Expression<Func<PromotionTrackingLink, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<PromotionTrackingLink>(l => l.OrganizationId);
}
