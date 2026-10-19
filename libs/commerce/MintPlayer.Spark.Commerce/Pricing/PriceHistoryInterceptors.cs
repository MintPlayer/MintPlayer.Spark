using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Commerce.Pricing;

/// <summary>
/// Records the price history (GA3) of every <see cref="ISellableItem"/> saved through Spark's pipeline, so no
/// storefront package can forget it. An edit whose price changed is recorded in the save's own session (it commits
/// with the change, or not at all); a new item, which has no id before it is stored, gets its first entry right after
/// the commit. Code that writes items outside the pipeline calls <see cref="ISparkPriceHistory.RecordAsync"/> itself.
/// </summary>
/// <remarks>A save of a sale price that the storefront announces as a reduction is recorded by the Promotions
/// package with <c>isAnnouncedReduction</c> (M8); this interceptor records plain changes.</remarks>
internal sealed class PriceHistoryInterceptors(ISparkPriceHistory history, IFinalPriceCalculator calculator)
    : IBeforeSave<ISellableItem>, IAfterSave<ISellableItem>
{
    private const string NewItemFact = "commerce.price-history.new";

    // Finalize: see the price as every other interceptor left it.
    public InterceptorStage Stage => InterceptorStage.Finalize;

    public async ValueTask OnBeforeSaveAsync(ISellableItem entity, SaveContext context)
    {
        if (entity.Price is null)
            return;
        if (context.Before is not ISellableItem before || string.IsNullOrEmpty(entity.Id))
        {
            context.Facts[NewItemFact] = "1";
            return;
        }
        if (before.Price == entity.Price && before.PriceBasis == entity.PriceBasis && before.VatRateBasisPoints == entity.VatRateBasisPoints
            && string.Equals(before.Channel, entity.Channel, StringComparison.Ordinal))
            return;
        Money? previous = before.Price is null ? null : calculator.Calculate(FinalPriceRequest.For(before)).UnitTotal;
        await history.RecordAsync(context.GetSession(), entity, previous, "edit");
    }

    public async ValueTask OnAfterSaveAsync(ISellableItem entity, SaveContext context)
    {
        if (!context.Facts.ContainsKey(NewItemFact) || entity.Price is null || string.IsNullOrEmpty(entity.Id))
            return;
        var session = context.GetSession();
        await history.RecordAsync(session, entity, null, "create");
        await session.SaveChangesAsync();
    }
}
