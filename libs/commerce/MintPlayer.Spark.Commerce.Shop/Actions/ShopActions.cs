using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Shop.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shop.Actions;

// Products, variants and shop orders are organization-owned (X8): Organizations' row policy limits every list and load
// to the members of the owning organization (Viewer reads, Member edits, Admin deletes and publishes), and a published
// product to everyone the type's right reaches. The storefront reads published products through
// /spark/commerce/shop/products and /catalog instead. Categories are the platform's (ShopCategoryInterceptor).

/// <summary>The query <c>ShopProducts</c>.</summary>
public partial class ShopProductActions : DefaultPersistentObjectActions<ShopProduct>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ShopProduct> ShopProducts() => session.Query<ShopProduct>();
}

/// <summary>The query <c>ShopVariants</c>.</summary>
public partial class ShopVariantActions : DefaultPersistentObjectActions<ShopVariant>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ShopVariant> ShopVariants() => session.Query<ShopVariant>();
}

/// <summary>The query <c>ShopCategories</c>.</summary>
public partial class ShopCategoryActions : DefaultPersistentObjectActions<ShopCategory>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ShopCategory> ShopCategories() => session.Query<ShopCategory>();
}

/// <summary>The query <c>ShopOrders</c> (read-only: the shop writes orders from order and shipment events).</summary>
public partial class ShopOrderActions : DefaultPersistentObjectActions<ShopOrder>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ShopOrder> ShopOrders() => session.Query<ShopOrder>();
}

/// <summary>The query <c>ShopProductReviews</c> (read-only: buyers write through the review endpoint, moderators decide there).</summary>
public partial class ShopProductReviewActions : DefaultPersistentObjectActions<ShopProductReview>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ShopProductReview> ShopProductReviews() => session.Query<ShopProductReview>();
}
