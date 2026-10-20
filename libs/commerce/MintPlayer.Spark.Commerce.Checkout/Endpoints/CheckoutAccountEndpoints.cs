using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Checkout.Endpoints;

// The customer account area's order endpoints (GB10, §4.14). Signed in only; an anonymous call answers the uniform 401.

/// <summary><c>GET /spark/commerce/checkout/account/orders?skip=&amp;take=</c>: the signed-in buyer's orders, newest first.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetAccountOrders : IGetEndpoint
{
    public static string Path => "/account/orders";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly CheckoutAccount account;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var query = httpContext.Request.Query;
        var skip = int.TryParse(query["skip"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var take = int.TryParse(query["take"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 20;
        return CheckoutResults.RunAsync(addOn, httpContext, async () => await account.OrdersAsync(skip, take, httpContext.RequestAborted));
    }
}

/// <summary><c>GET /spark/commerce/checkout/account/guest-orders</c>: what "attach guest orders" would move (D4).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetGuestOrders : IGetEndpoint
{
    public static string Path => "/account/guest-orders";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly CheckoutAccount account;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () => await account.GuestOrdersAsync(httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/checkout/account/guest-orders/attach</c>: moves them onto the account (confirmed e-mail only).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class AttachGuestOrders : IPostEndpoint<SparkAttachGuestOrdersRequest>
{
    public static string Path => "/account/guest-orders/attach";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly CheckoutAccount account;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkAttachGuestOrdersRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => await account.AttachGuestOrdersAsync(cancellationToken));
}
