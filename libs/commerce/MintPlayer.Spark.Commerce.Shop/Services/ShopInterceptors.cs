using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Services;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shop.Services;

/// <summary>
/// Saves of a product through the persistent-object pipeline: the slug (from the name when empty, unique), the default
/// channel, the timestamps, a channel that never changes once published, the maintained fields, the price history of
/// variants that take the product's price (GA3), and publication (X8, §4.10): when <c>IsPublished</c> turns on,
/// <see cref="ShopPublisher.PublishAsync"/> refuses a product that cannot sell and provisions its variants. The
/// organization's <c>Publish</c> role is Organizations' own gate. A product that was ever published is never deleted
/// (its orders name it): unpublish it.
/// </summary>
internal sealed class ShopProductInterceptor(
    ShopPublisher publisher,
    ISparkPriceHistory priceHistory,
    IFinalPriceCalculator calculator,
    IOptionsMonitor<SparkCommerceShopOptions> options,
    TimeProvider clock) : IBeforeSave<ShopProduct>, IBeforeDelete<ShopProduct>
{
    public async ValueTask OnBeforeSaveAsync(ShopProduct entity, SaveContext context)
    {
        var session = context.GetSession();
        var before = context.Before as ShopProduct;
        var now = clock.GetUtcNow();
        if (before is null)
            entity.CreatedAt = now;
        entity.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(entity.Channel))
            entity.Channel = options.CurrentValue.DefaultChannel;

        if (string.IsNullOrWhiteSpace(entity.Slug))
            entity.Slug = ShopSlugs.Slugify(entity.Name.GetValue(TranslatedString.DefaultLanguage));
        entity.Slug = entity.Slug.Trim().ToLowerInvariant();
        if (!ShopSlugs.IsSlug(entity.Slug))
            throw new SparkValidationException("Use lower-case letters, digits and dashes.", nameof(ShopProduct.Slug));
        if (before is null || before.Slug != entity.Slug)
        {
            var id = entity.Id;
            var slug = entity.Slug;
            if (await session.Query<ShopProduct>().Customize(c => c.WaitForNonStaleResults()).AnyAsync(p => p.Slug == slug && p.Id != id))
                throw new SparkValidationException("Another product uses this address.", nameof(ShopProduct.Slug));
        }
        if (before?.PublishedAt is not null && !string.Equals(before.Channel, entity.Channel, StringComparison.Ordinal))
            throw new SparkValidationException("The channel of a product that was published cannot change.", nameof(ShopProduct.Channel));
        entity.PublishedAt ??= before?.PublishedAt;
        entity.ManufacturerCountry = entity.ManufacturerCountry?.Trim().ToUpperInvariant();
        entity.OriginCountry = entity.OriginCountry?.Trim().ToUpperInvariant();
        foreach (var attribute in entity.Attributes)
            attribute.Name = attribute.Name.Trim().ToLowerInvariant();
        // The review summary is the shop's (ShopReviews), never the form's.
        entity.ReviewCount = before?.ReviewCount ?? 0;
        entity.RatingAverage = before?.RatingAverage;

        var variants = await ShopPublisher.VariantsAsync(session, entity.Id, CancellationToken.None);
        await ShopPublisher.RefreshAsync(session, entity, variants, CancellationToken.None);

        if (before is not null && (before.Price != entity.Price || before.VatRateBasisPoints != entity.VatRateBasisPoints || before.Channel != entity.Channel))
            foreach (var variant in variants.Where(v => v.Price is null))
            {
                Money? previous = before.Price is { } old
                    ? calculator.Calculate(FinalPriceRequest.For(ShopPriceItem.For(before, variant, old))).UnitTotal
                    : null;
                if (entity.Price is not null && entity.Channel.Length > 0)
                    await priceHistory.RecordAsync(session, ShopPriceItem.For(entity, variant, entity.Price), previous, "edit");
            }

        if (entity.IsPublished && before?.IsPublished != true)
        {
            if (string.IsNullOrEmpty(entity.Id))
                throw new SparkValidationException("Save the product with its variants first, then publish it.", nameof(ShopProduct.IsPublished));
            await publisher.PublishAsync(session, entity, variants, CancellationToken.None);
        }
    }

    public ValueTask OnBeforeDeleteAsync(ShopProduct entity, DeleteContext context)
    {
        if (entity.IsPublished || entity.PublishedAt is not null)
            throw new SparkValidationException("A product that was published is not deleted: unpublish it.", nameof(ShopProduct.IsPublished));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Saves of variants: the product's organization (X8, no join), the identifiers (SKU unique per seller, GS1-checked
/// GTIN), the price history (GA3), the quota of a variant of a published product (provisioned after the save), and the
/// product's maintained fields. A variant that was ever provisioned is never deleted (orders name it): deactivate it.
/// </summary>
internal sealed class ShopVariantInterceptor(
    ShopPublisher publisher,
    ISparkPriceHistory priceHistory,
    IFinalPriceCalculator calculator) : IBeforeSave<ShopVariant>, IAfterSave<ShopVariant>, IBeforeDelete<ShopVariant>
{
    public async ValueTask OnBeforeSaveAsync(ShopVariant entity, SaveContext context)
    {
        var session = context.GetSession();
        var before = context.Before as ShopVariant;
        if (before is not null && !string.Equals(before.ProductId, entity.ProductId, StringComparison.Ordinal))
            throw new SparkValidationException("A variant never moves to another product.", nameof(ShopVariant.ProductId));
        if (string.IsNullOrEmpty(entity.ProductId) || await session.LoadAsync<ShopProduct>(entity.ProductId) is not { } product)
            throw new SparkValidationException("Choose the product.", nameof(ShopVariant.ProductId));
        entity.OrganizationId = product.OrganizationId;
        entity.ProvisionedAt = before?.ProvisionedAt ?? entity.ProvisionedAt;
        entity.Sku = entity.Sku.Trim();
        entity.Gtin = string.IsNullOrWhiteSpace(entity.Gtin) ? null : entity.Gtin.Trim();
        entity.HsCode = string.IsNullOrWhiteSpace(entity.HsCode) ? null : entity.HsCode.Trim();
        entity.OriginCountry = string.IsNullOrWhiteSpace(entity.OriginCountry) ? null : entity.OriginCountry.Trim().ToUpperInvariant();
        foreach (var option in entity.Options)
            option.Name = option.Name.Trim().ToLowerInvariant();
        if (ProductRules.VariantProblems(product, entity).FirstOrDefault() is { } problem)
            throw new SparkValidationException(problem);

        var id = entity.Id;
        var sku = entity.Sku;
        var seller = entity.OrganizationId;
        if (await session.Query<ShopVariant>().Customize(c => c.WaitForNonStaleResults())
                .AnyAsync(v => v.Sku == sku && v.OrganizationId == seller && v.Id != id))
            throw new SparkValidationException("Another variant of this seller uses this SKU.", nameof(ShopVariant.Sku));
        if (before?.ProvisionedAt is not null && before.TrackInventory != entity.TrackInventory)
            throw new SparkValidationException("Whether a variant on sale counts its stock cannot change: add a new variant.", nameof(ShopVariant.TrackInventory));

        // GA3: every change of the effective price goes into the history, in the save's own session.
        var price = ProductRules.PriceOf(product, entity);
        var previousPrice = before is null ? null : ProductRules.PriceOf(product, before);
        if (price is not null && previousPrice != price && product.Channel.Length > 0)
        {
            if (string.IsNullOrEmpty(entity.Id))
                await session.StoreAsync(entity);
            Money? previousFinal = previousPrice is null ? null : calculator.Calculate(FinalPriceRequest.For(ShopPriceItem.For(product, entity, previousPrice))).UnitTotal;
            await priceHistory.RecordAsync(session, ShopPriceItem.For(product, entity, price), previousFinal, before is null ? "create" : "edit");
        }

        if (product.IsPublished && before?.ProvisionedAt is not null && before.Capacity != entity.Capacity)
            await publisher.ProvisionAsync(entity, CancellationToken.None);
    }

    public async ValueTask OnAfterSaveAsync(ShopVariant entity, SaveContext context)
    {
        var session = context.GetSession();
        if (await session.LoadAsync<ShopProduct>(entity.ProductId) is not { } product)
            return;
        if (product.IsPublished && entity.ProvisionedAt is null)
            await publisher.ProvisionAsync(entity, CancellationToken.None);
        var variants = await ShopPublisher.VariantsAsync(session, product.Id, CancellationToken.None);
        variants = variants.Where(v => v.Id != entity.Id).Append(entity).ToList();
        await ShopPublisher.RefreshAsync(session, product, variants, CancellationToken.None);
        await session.SaveChangesAsync();
    }

    public ValueTask OnBeforeDeleteAsync(ShopVariant entity, DeleteContext context)
    {
        if (entity.ProvisionedAt is not null)
            throw new SparkValidationException("A variant that was on sale is not deleted: deactivate it.", nameof(ShopVariant.IsActive));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Categories are the platform's: only platform admins (<c>ManageSellers/Commerce</c>) create, edit or delete them. The
/// slug is unique; a category never becomes its own ancestor; a category with children is not deleted.
/// </summary>
internal sealed class ShopCategoryInterceptor(IAccessControl accessControl) : IBeforeSave<ShopCategory>, IBeforeDelete<ShopCategory>
{
    public async ValueTask OnBeforeSaveAsync(ShopCategory entity, SaveContext context)
    {
        if (!context.IsSystemContext && !await accessControl.IsAllowedAsync(CommerceRights.ManageSellers))
            throw new SparkAccessDeniedException(CommerceRights.ManageSellers); // categories are edited by platform admins
        var session = context.GetSession();
        if (string.IsNullOrWhiteSpace(entity.Slug))
            entity.Slug = ShopSlugs.Slugify(entity.Name.GetValue(TranslatedString.DefaultLanguage), "category");
        entity.Slug = entity.Slug.Trim().ToLowerInvariant();
        if (!ShopSlugs.IsSlug(entity.Slug))
            throw new SparkValidationException("Use lower-case letters, digits and dashes.", nameof(ShopCategory.Slug));
        var id = entity.Id;
        var slug = entity.Slug;
        if (await session.Query<ShopCategory>().Customize(c => c.WaitForNonStaleResults()).AnyAsync(c => c.Slug == slug && c.Id != id))
            throw new SparkValidationException("Another category uses this address.", nameof(ShopCategory.Slug));
        entity.FacetKeys = entity.FacetKeys.Select(k => k.Trim().ToLowerInvariant()).Where(ProductRules.IsKey).Distinct(StringComparer.Ordinal).ToList();

        var parent = entity.ParentId;
        for (var depth = 0; parent is not null && depth < 16; depth++)
        {
            if (id is not null && parent == id)
                throw new SparkValidationException("A category cannot be its own ancestor.", nameof(ShopCategory.ParentId));
            parent = (await session.LoadAsync<ShopCategory>(parent))?.ParentId;
        }
    }

    public async ValueTask OnBeforeDeleteAsync(ShopCategory entity, DeleteContext context)
    {
        if (!context.IsSystemContext && !await accessControl.IsAllowedAsync(CommerceRights.ManageSellers))
            throw new SparkAccessDeniedException(CommerceRights.ManageSellers); // categories are edited by platform admins
        var id = entity.Id;
        if (await context.GetSession().Query<ShopCategory>().AnyAsync(c => c.ParentId == id))
            throw new SparkValidationException("Move or delete the subcategories first.");
    }
}
