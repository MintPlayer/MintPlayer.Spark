using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Shop.Account;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Shop.Endpoints;

// The customer account area's address book (GB10, §4.14). Signed in only; the book is always the caller's own.

/// <summary><c>GET /spark/commerce/shop/account/addresses</c>.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetAddressBook : IGetEndpoint
{
    public static string Path => "/account/addresses";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly ShopAddressBooks books;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () => await books.GetAsync(httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/shop/account/addresses</c>: adds or replaces an entry.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class SaveAddressBookEntry : IPostEndpoint<SparkShopAddressSaveRequest>
{
    public static string Path => "/account/addresses";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ShopAddressBooks books;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopAddressSaveRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await books.SaveAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/commerce/shop/account/addresses/delete</c>.</summary>
[MemberOf<ShopGroup>]
internal sealed partial class DeleteAddressBookEntry : IPostEndpoint<SparkShopAddressDeleteRequest>
{
    public static string Path => "/account/addresses/delete";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ShopAddressBooks books;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkShopAddressDeleteRequest request, CancellationToken cancellationToken)
        => ShopResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.EntryId is { Length: > 0 } id ? await books.DeleteAsync(id, cancellationToken) : null);
}
