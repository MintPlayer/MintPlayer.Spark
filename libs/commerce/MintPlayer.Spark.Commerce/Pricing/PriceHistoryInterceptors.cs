using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Commerce.Pricing;

/// <summary>
/// Records the price history (GA3) of every <see cref="ISellableItem"/> saved through Spark's pipeline, so no
/// storefront package can forget it. Every entry, a new item's first one included, is recorded in the save's own
/// session: it commits with the price, or not at all. A new item has no id yet; storing it here lets the session
/// assign the id the pipeline then keeps (storing a tracked entity again is a no-op), the same as Ticketing.Events'
/// phase history. Code that writes items outside the pipeline calls <see cref="ISparkPriceHistory.RecordAsync"/> itself.
/// </summary>
/// <remarks>A save of a sale price that the storefront announces as a reduction is recorded by the Promotions
/// package with <c>isAnnouncedReduction</c> (M8); this interceptor records plain changes.</remarks>
internal sealed class PriceHistoryInterceptors(ISparkPriceHistory history, IFinalPriceCalculator calculator)
    : IBeforeSave<ISellableItem>
{
    // Finalize: see the price as every other interceptor left it.
    public InterceptorStage Stage => InterceptorStage.Finalize;

    public async ValueTask OnBeforeSaveAsync(ISellableItem entity, SaveContext context)
    {
        if (entity.Price is null)
            return;
        var session = context.GetSession();
        if (context.Before is not ISellableItem before || string.IsNullOrEmpty(entity.Id))
        {
            if (string.IsNullOrEmpty(entity.Id))
                await session.StoreAsync(entity);
            await history.RecordAsync(session, entity, null, "create");
            return;
        }
        if (before.Price == entity.Price && before.PriceBasis == entity.PriceBasis && before.VatRateBasisPoints == entity.VatRateBasisPoints
            && string.Equals(before.Channel, entity.Channel, StringComparison.Ordinal))
            return;
        Money? previous = before.Price is null ? null : calculator.Calculate(FinalPriceRequest.For(before)).UnitTotal;
        await history.RecordAsync(session, entity, previous, "edit");
    }
}
