using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shop.Endpoints;

/// <summary>
/// <c>GET /spark/commerce/shop/item?id=shop:{variantId}</c>: where a checkout item id lives on the storefront (M17), the
/// published product's slug and the variant. Promotions' back-in-stock mails link <c>/shop/item?id={itemId}</c>
/// (<c>ItemPagePath</c>); the storefront's item route asks here and opens the product page. Anonymous; a variant of an
/// unpublished product answers the missing-row refusal.
/// </summary>
[MemberOf<ShopGroup>]
internal sealed partial class GetShopItemLink : IGetEndpoint
{
    public static string Path => "/item";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptionsMonitor<SparkCommerceShopOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ShopResults.RunAsync(addOn, httpContext, async () =>
        {
            if (!options.CurrentValue.PublicPages || ShopItemIds.VariantId(httpContext.Request.Query["id"].ToString()) is not { Length: > 0 } variantId)
                return null;
            using var session = store.OpenAsyncSession();
            var variant = await session.LoadAsync<ShopVariant>(variantId, httpContext.RequestAborted);
            if (variant is null)
                return null;
            var product = await session.LoadAsync<ShopProduct>(variant.ProductId, httpContext.RequestAborted);
            return product is { IsPublished: true } ? new SparkShopItemLink(product.Slug, variant.Id!) : null;
        });
}
