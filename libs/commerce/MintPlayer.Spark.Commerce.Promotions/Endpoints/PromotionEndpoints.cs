using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Promotions.Contracts;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions.Endpoints;

internal class PromotionsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/promotions";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>Maps refusals: a <see cref="PromotionException"/> as 409 with <see cref="SparkPromotionRefusal"/>; null (not found, not yours) as the standard refusal (#453).</summary>
internal static class PromotionResults
{
    public static async Task<IResult> RunAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<object?>> action)
    {
        try
        {
            var result = await action();
            return result is null ? addOn.Refusal(http) : addOn.Envelope(result, StatusCodes.Status200OK);
        }
        catch (PromotionException ex)
        {
            return addOn.Envelope(new SparkPromotionRefusal(ex.Code, ex.Subject), StatusCodes.Status409Conflict);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return addOn.Envelope(new SparkPromotionRefusal("conflict", null), StatusCodes.Status409Conflict);
        }
    }

    public static SparkSalePriceView View(PromotionSalePrice s)
        => new(s.Id!, s.ItemId, s.SalePrice, s.RegularPrice, s.ValidFrom, s.ValidUntil, s.SeriesStart, s.PerishableExempt, s.Cancelled);

    public static SparkWaitingListView View(PromotionWaitingList l) => new(l.Id!, l.ItemId, l.Title, l.Status.ToString(), l.ActivatedAt);

    public static SparkWaitingListEntryView View(PromotionWaitingListEntry e)
        => new(e.ListId, e.Status.ToString(), e.Quantity, e.Position, e.Status == PromotionWaitingListEntryStatus.Offered ? e.OfferCode : null, e.OfferExpiresAt);
}

// ---- public ----------------------------------------------------------------------------------------------------

/// <summary>
/// <c>GET /spark/commerce/promotions/code?code=</c>: what a code does, for the storefront (the code is the secret: it is
/// shown only to someone who typed it). A personal code is answered only to its owner.
/// </summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetPromotionCode : IGetEndpoint
{
    public static string Path => "/code";

    [Inject] private readonly ISparkPromotionCodes codes;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly TimeProvider clock;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () =>
        {
            var code = await codes.FindAsync(httpContext.Request.Query["code"].ToString(), httpContext.RequestAborted);
            if (code is null || !PromotionRules.IsLive(code, clock.GetUtcNow()))
                return null;
            if (code.AssignedToUserId is not null && !string.Equals(code.AssignedToUserId, currentUser.Id, StringComparison.Ordinal))
                return null;
            var blocked = code.Effect == PromotionEffect.CapacityBlock ? code.ItemIds.Select(i => PromotionItemIds.Blocked(code.Code, i)).ToList() : [];
            return new SparkPromotionCodeInfo(code.Code, code.Effect.ToString(), code.PercentBasisPoints, code.Amount, code.ItemIds, blocked,
                code.MaxQuantityPerOrder, code.ValidUntil);
        });
}

/// <summary>
/// <c>GET /spark/commerce/promotions/sale-price?itemId=</c>: the price display during a sale, with the Omnibus prior price
/// (GA3). Only for an item checkout sells now (resolved through the capacity sources): an unpublished item answers the refusal.
/// </summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetSalePrice : IGetEndpoint
{
    public static string Path => "/sale-price";

    [Inject] private readonly ISparkSalePrices salePrices;
    [Inject] private readonly PromotionItems items;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () =>
        {
            var itemId = httpContext.Request.Query["itemId"].ToString();
            var item = itemId.Length == 0 ? null : await items.ResolveAsync(itemId, 1, httpContext.RequestAborted);
            // An item behind an unlock code is hidden: its price is not public either.
            if (item is null || item.AdmissionScope?.StartsWith(PromotionItemIds.Prefix, StringComparison.Ordinal) == true)
                return null;
            return await salePrices.DisplayAsync(item, httpContext.RequestAborted);
        });
}

/// <summary><c>POST /spark/commerce/promotions/track</c>: a visit through a tracking link (GC2). Anonymous; stored only with consent where required.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class TrackVisit : IPostEndpoint<SparkTrackRequest>
{
    public static string Path => "/track";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly Tracking tracking;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTrackRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.Ref is null ? null : new SparkTrackResult(await tracking.CaptureAsync(request, cancellationToken)));
}

/// <summary><c>GET /spark/commerce/promotions/waiting-lists/get?id=</c>: a list's public state (and the caller's entry, when signed in).</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetWaitingList : IGetEndpoint
{
    public static string Path => "/waiting-lists/get";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () =>
        {
            using var session = store.OpenAsyncSession();
            var id = httpContext.Request.Query["id"].ToString();
            return id.StartsWith("PromotionWaitingLists/", StringComparison.Ordinal) && await session.LoadAsync<PromotionWaitingList>(id, httpContext.RequestAborted) is { } list
                ? PromotionResults.View(list)
                : null;
        });
}

/// <summary><c>POST /spark/commerce/promotions/waiting-lists/join</c>: ticket mode needs an account.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class JoinWaitingList : IPostEndpoint<SparkWaitingListJoinRequest>
{
    public static string Path => "/waiting-lists/join";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly WaitingLists lists;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkWaitingListJoinRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.ListId is null || !currentUser.IsAuthenticated || currentUser.Id is not { } me ? null
                : PromotionResults.View(await lists.JoinAsync(request.ListId, me, request.Quantity, cancellationToken)));
}

/// <summary><c>POST /spark/commerce/promotions/waiting-lists/leave</c>.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class LeaveWaitingList : IPostEndpoint<SparkPromotionIdRequest>
{
    public static string Path => "/waiting-lists/leave";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly WaitingLists lists;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionIdRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.Id is null || currentUser.Id is not { } me || !await lists.LeaveAsync(request.Id, me, cancellationToken) ? null : new { left = true });
}

/// <summary><c>GET /spark/commerce/promotions/waiting-lists/mine?id=</c>: the signed-in buyer's entry on a list.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetMyWaitingListEntry : IGetEndpoint
{
    public static string Path => "/waiting-lists/mine";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () =>
        {
            if (currentUser.Id is not { } me || httpContext.Request.Query["id"].ToString() is not { Length: > 0 } listId)
                return null;
            using var session = store.OpenAsyncSession();
            var entry = await session.LoadAsync<PromotionWaitingListEntry>(PromotionWaitingListEntry.IdFor(listId, me), httpContext.RequestAborted);
            return entry is null ? null : PromotionResults.View(entry);
        });
}

/// <summary><c>POST /spark/commerce/promotions/back-in-stock/subscribe</c> (GB11). Anonymous: a guest confirms the address by mail.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class SubscribeBackInStock : IPostEndpoint<SparkBackInStockRequest>
{
    public static string Path => "/back-in-stock/subscribe";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackInStock backInStock;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBackInStockRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.ItemId is null)
                return null;
            var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            var (subscription, sent) = await backInStock.SubscribeAsync(request.ItemId, currentUser.IsAuthenticated ? currentUser.Id : null, request.Email,
                culture.Length == 0 ? null : culture, cancellationToken);
            return new SparkBackInStockResult(subscription.Id!, subscription.Confirmed, sent);
        });
}

/// <summary><c>POST /spark/commerce/promotions/back-in-stock/confirm</c>: the double opt-in link.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class ConfirmBackInStock : IPostEndpoint<SparkBackInStockTokenRequest>
{
    public static string Path => "/back-in-stock/confirm";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackInStock backInStock;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBackInStockTokenRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.Id is null || request.Token is null || !await backInStock.ConfirmAsync(request.Id, request.Token, cancellationToken) ? null : new { confirmed = true });
}

/// <summary><c>POST /spark/commerce/promotions/back-in-stock/unsubscribe</c>: with the token of a mail, no sign-in needed.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class UnsubscribeBackInStock : IPostEndpoint<SparkBackInStockTokenRequest>
{
    public static string Path => "/back-in-stock/unsubscribe";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackInStock backInStock;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBackInStockTokenRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.Id is null || request.Token is null || !await backInStock.UnsubscribeAsync(request.Id, request.Token, cancellationToken) ? null : new { unsubscribed = true });
}

/// <summary><c>GET /spark/commerce/promotions/back-in-stock/mine</c>: the signed-in buyer's subscriptions (GB10 account area).</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetMyBackInStock : IGetEndpoint
{
    public static string Path => "/back-in-stock/mine";

    [Inject] private readonly BackInStock backInStock;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () => currentUser.Id is not { } me ? null
            : (await backInStock.MineAsync(me, httpContext.RequestAborted))
                .Select(s => new SparkBackInStockView(s.Id!, s.ItemId, s.CreatedAt, s.ExpiresAt, s.NotifiedAt is not null)).ToList());
}

/// <summary><c>POST /spark/commerce/promotions/back-in-stock/delete</c>: one of the signed-in buyer's own subscriptions.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class DeleteMyBackInStock : IPostEndpoint<SparkPromotionIdRequest>
{
    public static string Path => "/back-in-stock/delete";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackInStock backInStock;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionIdRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.Id is null || currentUser.Id is not { } me || !await backInStock.DeleteMineAsync(me, request.Id, cancellationToken) ? null : new { deleted = true });
}

// ---- admin (seller staff, platform admins) ----------------------------------------------------------------------

/// <summary><c>POST /spark/commerce/promotions/admin/codes/save</c>: create or update a code of a seller the caller manages.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class SaveCode : IPostEndpoint<SparkPromotionCodeRequest>
{
    public static string Path => "/admin/codes/save";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkPromotionCodes codes;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly PromotionItems items;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionCodeRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.Code is null || !await access.MayManageAsync(request.OrganizationId, cancellationToken))
                return null;
            // A seller's code may name only that seller's items.
            if (request.OrganizationId is not null)
            {
                foreach (var itemId in request.ItemIds ?? [])
                {
                    var item = await items.ResolveAsync(itemId, 1, cancellationToken);
                    if (item is not null && !string.Equals(item.OrganizationId, request.OrganizationId, StringComparison.Ordinal))
                        return null;
                }
            }
            var code = await codes.SaveAsync(request, access.CallerId, cancellationToken);
            return new { code = code.Code };
        });
}

/// <summary><c>POST /spark/commerce/promotions/admin/codes/deactivate</c>.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class DeactivateCode : IPostEndpoint<SparkPromotionCodeKey>
{
    public static string Path => "/admin/codes/deactivate";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkPromotionCodes codes;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionCodeKey request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.Code is null || await codes.FindAsync(request.Code, cancellationToken) is not { } code
                || !await access.MayManageAsync(code.OrganizationId, cancellationToken))
                return null;
            await codes.DeactivateAsync(code.Code, cancellationToken);
            return new { code = code.Code, active = false };
        });
}

/// <summary><c>GET /spark/commerce/promotions/admin/codes/get?code=</c>: a code with its uses and block.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetCodeAdmin : IGetEndpoint
{
    public static string Path => "/admin/codes/get";

    [Inject] private readonly ISparkPromotionCodes codes;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly PromotionUsage usage;
    [Inject] private readonly PromotionBlocks blocks;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () =>
        {
            var ct = httpContext.RequestAborted;
            if (await codes.FindAsync(httpContext.Request.Query["code"].ToString(), ct) is not { } code || !await access.MayManageAsync(code.OrganizationId, ct))
                return null;
            var block = code.BlockId is null ? null : await blocks.LoadAsync(code.BlockId, ct);
            var available = block is null ? (int?)null : (await blocks.AvailabilityAsync(block, ct)).Available;
            return new SparkPromotionCodeView(code.Code, code.OrganizationId, code.Effect.ToString(), code.Active, code.ValidFrom, code.ValidUntil,
                code.MaxUses, await usage.CountAsync(code, ct), block?.Capacity, available);
        });
}

/// <summary><c>POST /spark/commerce/promotions/admin/sale-prices/schedule</c> (GA3).</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class ScheduleSalePrice : IPostEndpoint<SparkSalePriceRequest>
{
    public static string Path => "/admin/sale-prices/schedule";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkSalePrices salePrices;
    [Inject] private readonly PromotionItems items;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkSalePriceRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.ItemId is null || request.SalePrice is null || await items.ResolveAsync(request.ItemId, 1, cancellationToken) is not { } item
                || !await access.MayManageAsync(item.OrganizationId, cancellationToken))
                return null;
            var sale = await salePrices.ScheduleAsync(new SalePriceRequest(request.ItemId, request.SalePrice, request.ValidFrom, request.ValidUntil, request.PerishableExempt),
                access.CallerId, cancellationToken);
            return PromotionResults.View(sale);
        });
}

/// <summary><c>POST /spark/commerce/promotions/admin/sale-prices/cancel</c>: ends a running sale now, or cancels a future one.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class CancelSalePrice : IPostEndpoint<SparkPromotionIdRequest>
{
    public static string Path => "/admin/sale-prices/cancel";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkSalePrices salePrices;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionIdRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.Id is null || !request.Id.StartsWith("PromotionSalePrices/", StringComparison.Ordinal))
                return null;
            using (var session = store.OpenAsyncSession())
            {
                if (await session.LoadAsync<PromotionSalePrice>(request.Id, cancellationToken) is not { } sale || !await access.MayManageAsync(sale.OrganizationId, cancellationToken))
                    return null;
            }
            return await salePrices.CancelAsync(request.Id, cancellationToken) is { } cancelled ? PromotionResults.View(cancelled) : null;
        });
}

/// <summary><c>POST /spark/commerce/promotions/admin/waiting-lists/create</c>.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class CreateWaitingList : IPostEndpoint<SparkWaitingListRequest>
{
    public static string Path => "/admin/waiting-lists/create";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly WaitingLists lists;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkWaitingListRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.ItemId is null || !await access.MayManageAsync(request.OrganizationId, cancellationToken) ? null
                : PromotionResults.View(await lists.CreateAsync(request.ItemId, request.OrganizationId, request.Title, request.QueueScope,
                    request.OfferValidityMinutes is int minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes) : null, cancellationToken)));
}

/// <summary><c>POST /spark/commerce/promotions/admin/waiting-lists/close-queue</c>: the queue closed (without a bridge over the waiting room).</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class CloseWaitingListQueue : IPostEndpoint<SparkPromotionIdRequest>
{
    public static string Path => "/admin/waiting-lists/close-queue";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly WaitingLists lists;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionIdRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (await WaitingListAdmin.LoadManagedAsync(store, access, request?.Id, cancellationToken) is not { } list)
                return null;
            await lists.CloseQueueAsync(list.Id!, cancellationToken);
            return new { queueClosed = true };
        });
}

/// <summary><c>POST /spark/commerce/promotions/admin/waiting-lists/close</c>: ends the list; spare units go back to sale.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class CloseWaitingList : IPostEndpoint<SparkPromotionIdRequest>
{
    public static string Path => "/admin/waiting-lists/close";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly WaitingLists lists;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPromotionIdRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (await WaitingListAdmin.LoadManagedAsync(store, access, request?.Id, cancellationToken) is not { } list)
                return null;
            await lists.CloseAsync(list.Id!, cancellationToken);
            return new { closed = true };
        });
}

internal static class WaitingListAdmin
{
    public static async Task<PromotionWaitingList?> LoadManagedAsync(IDocumentStore store, PromotionsAccess access, string? id, CancellationToken cancellationToken)
    {
        if (id is null || !id.StartsWith("PromotionWaitingLists/", StringComparison.Ordinal))
            return null;
        using var session = store.OpenAsyncSession();
        var list = await session.LoadAsync<PromotionWaitingList>(id, cancellationToken);
        return list is not null && await access.MayManageAsync(list.OrganizationId, cancellationToken) ? list : null;
    }
}

/// <summary><c>POST /spark/commerce/promotions/admin/links/save</c> (GC2).</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class SaveTrackingLink : IPostEndpoint<SparkTrackingLinkRequest>
{
    public static string Path => "/admin/links/save";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTrackingLinkRequest request, CancellationToken cancellationToken)
        => PromotionResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.Ref is null || !await access.MayManageAsync(request.OrganizationId, cancellationToken))
                return null;
            var reference = PromotionRules.NormalizeCode(request.Ref) ?? throw new PromotionException(PromotionRefusals.InvalidRequest, "ref");
            if (request.CommissionBasisPoints is < 0 or > 10_000 || request.CommissionFixed?.ToMinorUnits() < 0)
                throw new PromotionException(PromotionRefusals.InvalidRequest, "commission");
            using var session = store.OpenAsyncSession();
            var id = PromotionTrackingLink.IdFor(reference);
            var link = await session.LoadAsync<PromotionTrackingLink>(id, cancellationToken);
            if (link is not null && !string.Equals(link.OrganizationId, request.OrganizationId, StringComparison.Ordinal))
                throw new PromotionException(PromotionRefusals.CodeTaken, reference);
            if (link is null)
            {
                link = new PromotionTrackingLink { Id = id, Ref = reference, OrganizationId = request.OrganizationId, CreatedAt = clock.GetUtcNow() };
                await session.StoreAsync(link, changeVector: string.Empty, id, cancellationToken);
            }
            link.Name = request.Name ?? reference;
            link.PromoterUserId = request.PromoterUserId;
            link.UtmSource = request.UtmSource;
            link.UtmMedium = request.UtmMedium;
            link.UtmCampaign = request.UtmCampaign;
            link.UtmContent = request.UtmContent;
            link.CommissionBasisPoints = request.CommissionBasisPoints;
            link.CommissionFixed = request.CommissionFixed;
            link.Active = request.Active;
            await session.SaveChangesAsync(cancellationToken);
            return new { @ref = reference };
        });
}

/// <summary><c>GET /spark/commerce/promotions/admin/links/report?ref=</c>: the seller's staff, or the promoter of the link.</summary>
[MemberOf<PromotionsGroup>]
internal sealed partial class GetTrackingReport : IGetEndpoint
{
    public static string Path => "/admin/links/report";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly Tracking tracking;
    [Inject] private readonly PromotionsAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => PromotionResults.RunAsync(addOn, httpContext, async () =>
        {
            var ct = httpContext.RequestAborted;
            if (PromotionRules.NormalizeCode(httpContext.Request.Query["ref"].ToString()) is not { } reference)
                return null;
            PromotionTrackingLink? link;
            using (var session = store.OpenAsyncSession())
                link = await session.LoadAsync<PromotionTrackingLink>(PromotionTrackingLink.IdFor(reference), ct);
            if (link is null)
                return null;
            var promoter = link.PromoterUserId is not null && string.Equals(link.PromoterUserId, access.CallerId, StringComparison.Ordinal);
            if (!promoter && !await access.MayManageAsync(link.OrganizationId, ct))
                return null;
            return await tracking.ReportAsync(reference, ct);
        });
}
