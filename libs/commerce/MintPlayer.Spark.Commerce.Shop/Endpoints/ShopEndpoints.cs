using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Delivery;
using MintPlayer.Spark.Commerce.Shop.Feeds;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Commerce.Shop.Reviews;
using MintPlayer.Spark.Commerce.Shop.Services;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Shop.Endpoints;

internal class ShopGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/shop";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>
/// Maps refusals: a shop or checkout code as 409 with <see cref="SparkShopRefusal"/>; a booking Shipping refused as 409
/// <c>shippingRefused</c>; a validation error as 400; "not yours" and "not found" as the missing-row refusal (401
/// anonymous, 404 otherwise, #453).
/// </summary>
internal static class ShopResults
{
    public static async Task<IResult> RunAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<object?>> action)
    {
        try
        {
            var result = await action();
            return result is null ? addOn.Refusal(http) : addOn.Envelope(result, StatusCodes.Status200OK);
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(http);
        }
        catch (ShopException ex) when (ex.Code is ShopRefusals.Forbidden or ShopRefusals.OrderNotFound or ShopRefusals.ProductNotFound)
        {
            return addOn.Refusal(http);
        }
        catch (ShopException ex)
        {
            return addOn.Envelope(new SparkShopRefusal(ex.Code, ex.Subject), StatusCodes.Status409Conflict);
        }
        catch (CheckoutException ex) when (ex.Code is CheckoutRefusals.Forbidden or CheckoutRefusals.OrderNotFound)
        {
            return addOn.Refusal(http);
        }
        catch (CheckoutException ex)
        {
            return addOn.Envelope(new SparkShopRefusal(ex.Code, ex.Subject), StatusCodes.Status409Conflict);
        }
        catch (ShippingRequestException)
        {
            return addOn.Envelope(new SparkShopRefusal("shippingRefused", null), StatusCodes.Status409Conflict);
        }
        catch (CarrierException ex)
        {
            return addOn.Envelope(new SparkShopRefusal("carrierFailed", ex.Carrier), StatusCodes.Status409Conflict);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
    }

    public static string Query(HttpContext http, string name) => http.Request.Query[name].ToString();

    public static int Int(HttpContext http, string name, int fallback)
        => int.TryParse(Query(http, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static string? Optional(HttpContext http, string name) => Query(http, name) is { Length: > 0 } value ? value : null;
}

// ---- storefront (anonymous) -------------------------------------------------------------------------------------------

/// <summary><c>GET /spark/commerce/shop/products?slug=</c>: a published product's page data. Anonymous.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetShopProduct : IGetEndpoint
{
    public static string Path => "/products";

    [Inject] private readonly ISparkShopCatalog catalog;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await catalog.GetProductAsync(ShopResults.Query(httpContext, "slug"), httpContext.RequestAborted));
}

/// <summary>
/// <c>GET /spark/commerce/shop/catalog?q=&amp;category=&amp;facet=colour:red&amp;facet=…&amp;price=&amp;sort=&amp;seller=&amp;skip=&amp;take=</c>:
/// a page of published products with facet counts (GB13). Anonymous.
/// </summary>
[MemberOf<ShopGroup>]
internal sealed partial class SearchShopCatalog : IGetEndpoint
{
    public static string Path => "/catalog";

    [Inject] private readonly ISparkShopCatalog catalog;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var price = ShopResults.Int(httpContext, "price", -1);
        var query = new ShopCatalogQuery(
            Search: ShopResults.Optional(httpContext, "q"),
            CategoryId: ShopResults.Optional(httpContext, "category"),
            Facets: httpContext.Request.Query["facet"].OfType<string>().ToList(),
            PriceRange: price >= 0 ? price : null,
            Sort: ShopResults.Optional(httpContext, "sort"),
            SellerId: ShopResults.Optional(httpContext, "seller"),
            Skip: ShopResults.Int(httpContext, "skip", 0),
            Take: ShopResults.Int(httpContext, "take", 24));
        return ShopResults.RunAsync(addOn, httpContext, async () => await catalog.SearchAsync(query, httpContext.RequestAborted));
    }
}

/// <summary><c>GET /spark/commerce/shop/categories</c>: every category in tree order. Anonymous.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetShopCategories : IGetEndpoint
{
    public static string Path => "/categories";

    [Inject] private readonly ISparkShopCatalog catalog;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await catalog.GetCategoriesAsync(httpContext.RequestAborted));
}

/// <summary><c>GET /spark/commerce/shop/reviews?productId=&amp;skip=&amp;take=</c>: a product's published reviews (GC5). Anonymous.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetShopReviews : IGetEndpoint
{
    public static string Path => "/reviews";

    [Inject] private readonly ISparkShopReviews reviews;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await reviews.GetAsync(ShopResults.Query(httpContext, "productId"),
            ShopResults.Int(httpContext, "skip", 0), ShopResults.Int(httpContext, "take", 20), httpContext.RequestAborted));
}

/// <summary>
/// <c>GET /spark/commerce/shop/feeds/google?seller=</c> and <c>/feeds/meta?seller=</c>: a seller's product feed (GC6) at a
/// stable URL (<c>seller</c> omitted or <c>platform</c>: the platform's own products). Anonymous, cached.
/// </summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetShopFeed : IGetEndpoint
{
    public static string Path => "/feeds/{kind}";

    [Inject] private readonly ISparkShopFeeds feeds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IOptionsMonitor<SparkCommerceShopOptions> options;
    [Inject] private readonly IServiceProvider services;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!options.CurrentValue.Feeds.Enabled || !Enum.TryParse<ShopFeedKind>(httpContext.Request.RouteValues["kind"] as string, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            return addOn.Refusal(httpContext);
        var origin = SparkPublicBaseUrl.Resolve(null, services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), httpContext);
        var feed = await feeds.GetAsync(kind, ShopResults.Optional(httpContext, "seller"), origin, httpContext.RequestAborted);
        return Results.File(feed.Content, feed.ContentType, feed.FileName);
    }
}

// ---- the buyer -------------------------------------------------------------------------------------------------------

/// <summary><c>GET /spark/commerce/shop/delivery/options?country=&amp;postalCode=</c>: the rates for the caller's basket and the current choice.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetDeliveryOptions : IGetEndpoint
{
    public static string Path => "/delivery/options";

    [Inject] private readonly ISparkShopDelivery delivery;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await delivery.GetOptionsAsync(
            ShopResults.Query(httpContext, "country"), ShopResults.Optional(httpContext, "postalCode"), httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/shop/delivery</c>: the buyer's delivery choice (address, method, service point). A guest gets the visitor cookie (X1).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class ChooseDelivery : IPostEndpoint<SparkShopDeliveryRequest>
{
    public static string Path => "/delivery";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkShopDelivery delivery;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopDeliveryRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await delivery.ChooseAsync(request, cancellationToken));
}

/// <summary><c>GET /spark/commerce/shop/orders?number=</c>: the shop's side of the caller's own order (shipments, tracking, delivery).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetOwnShopOrder : IGetEndpoint
{
    public static string Path => "/orders";

    [Inject] private readonly ISparkShopOrders orders;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await orders.GetOwnAsync(ShopResults.Query(httpContext, "number"), httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/shop/reviews</c>: a verified-purchase review of a delivered line of the caller's own order (GC5).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class SubmitShopReview : IPostEndpoint<SparkShopReviewRequest>
{
    public static string Path => "/reviews";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkShopReviews reviews;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopReviewRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await reviews.SubmitAsync(request, cancellationToken));
}

// ---- moderators ------------------------------------------------------------------------------------------------------

/// <summary><c>GET /spark/commerce/shop/reviews/pending?skip=&amp;take=</c>: reviews waiting for a moderator (<c>Review/Moderation</c>).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetPendingShopReviews : IGetEndpoint
{
    public static string Path => "/reviews/pending";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly ISparkShopReviews reviews;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await reviews.GetPendingAsync(ShopResults.Int(httpContext, "skip", 0), ShopResults.Int(httpContext, "take", 50), httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/shop/reviews/decide</c>: publish or reject a pending review (<c>Review/Moderation</c>).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class DecideShopReview : IPostEndpoint<SparkShopReviewDecision>
{
    public static string Path => "/reviews/decide";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkShopReviews reviews;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopReviewDecision request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await reviews.DecideAsync(request, cancellationToken));
}

// ---- staff (the seller's members, platform admins) -------------------------------------------------------------------

/// <summary><c>POST /spark/commerce/shop/staff/ship</c>: books a shipment of some units with one label per parcel (ShopOnly, GB5).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class ShipShopOrder : IPostEndpoint<SparkShopShipRequest>
{
    public static string Path => "/staff/ship";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkShopOrders orders;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopShipRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await orders.ShipAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/commerce/shop/staff/inspect</c>: the disposition of returned goods, with any diminished-value deduction (GA8, CRD Art. 14(2)).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class InspectShopReturn : IPostEndpoint<SparkShopInspectRequest>
{
    public static string Path => "/staff/inspect";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkShopOrders orders;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopInspectRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request is null)
                return null;
            await orders.InspectAsync(request, cancellationToken);
            return new SparkShopInspectResult(request.ReturnId, request.Disposition);
        });
}

/// <summary><c>POST /spark/commerce/shop/staff/return-label</c>: books and mails the return label of an expected return (ShopOnly, GB5).</summary>
[MemberOf<ShopGroup>]
internal sealed partial class CreateShopReturnLabel : IPostEndpoint<SparkShopReturnLabelRequest>
{
    public static string Path => "/staff/return-label";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkShopOrders orders;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopReturnLabelRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request is null ? null : new SparkShopShipResult(await orders.CreateReturnLabelAsync(request, cancellationToken), []));
}
