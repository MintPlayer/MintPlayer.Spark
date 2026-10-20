using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Promotions.Jobs;

/// <summary>
/// GA3: appends sale starts and ends to Commerce's price history (<see cref="ISparkPriceHistory"/>), so the Omnibus prior
/// price of a later reduction sees them (a sale price is the lowest price of its window while it runs). The start is an
/// announced reduction: its prior price is stored with the entry.
/// </summary>
internal sealed class SalePriceJob(
    IDocumentStore store,
    ISparkPriceHistory history,
    IFinalPriceCalculator calculator,
    PromotionItems items,
    TimeProvider clock,
    ILogger<SalePriceJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        using var session = store.OpenAsyncSession();
        var starting = await session.Query<PromotionSalePrice>()
            .Where(s => s.StartRecordedAt == null && !s.Cancelled && s.ValidFrom <= now)
            .Take(256).ToListAsync(cancellationToken);
        foreach (var sale in starting)
        {
            var regular = calculator.Calculate(new FinalPriceRequest(sale.Channel, sale.RegularPrice, sale.PriceBasis, sale.VatRateBasisPoints)).UnitTotal;
            await history.RecordAsync(session, Sellable(sale, sale.SalePrice), regular, "sale-start", isAnnouncedReduction: true, cancellationToken);
            sale.StartRecordedAt = now;
        }
        var ending = await session.Query<PromotionSalePrice>()
            .Where(s => s.StartRecordedAt != null && s.EndRecordedAt == null && s.ValidUntil <= now)
            .Take(256).ToListAsync(cancellationToken);
        foreach (var sale in ending)
        {
            // The price after the sale is the item's price now (it may have changed meanwhile); the snapshot otherwise.
            var current = await items.ResolveAsync(sale.ItemId, 1, cancellationToken);
            var after = current is not null && current.Price.Currency == sale.SalePrice.Currency ? current.Price : sale.RegularPrice;
            var saleFinal = calculator.Calculate(new FinalPriceRequest(sale.Channel, sale.SalePrice, sale.PriceBasis, sale.VatRateBasisPoints)).UnitTotal;
            await history.RecordAsync(session, Sellable(sale, after), saleFinal, "sale-end", cancellationToken: cancellationToken);
            sale.EndRecordedAt = now;
        }
        if (starting.Count + ending.Count > 0)
        {
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Recorded {Starts} sale start(s) and {Ends} end(s) in the price history.", starting.Count, ending.Count);
        }
    }

    private static PricedSellable Sellable(PromotionSalePrice sale, MintPlayer.Spark.Abstractions.Money price)
        => new(sale.ItemId, sale.Channel, sale.OrganizationId, price, sale.PriceBasis, sale.VatRateBasisPoints);
}

/// <summary>
/// The waiting lists and blocks: evaluates every open or active list (activation, freed units, offers, lapsed offers),
/// closes the blocks of codes that ended and returns their spare units, keeps returning what closed blocks get back
/// (refunds), and finishes block steps a crash left pending.
/// </summary>
internal sealed class PromotionBlocksJob(
    IDocumentStore store,
    WaitingLists waitingLists,
    PromotionBlocks blocks,
    TimeProvider clock,
    ILogger<PromotionBlocksJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "*/30 * * * * *";

    /// <summary>How long a closed block keeps being reconciled (refunds after the close return its units).</summary>
    internal static readonly TimeSpan ReconcileClosedFor = TimeSpan.FromDays(400);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<string> listIds;
        List<string> pending;
        List<string> ending;
        List<string> closed;
        var since = now - ReconcileClosedFor;
        using (var session = store.OpenAsyncSession())
        {
            listIds = await session.Query<PromotionWaitingList>().Where(l => l.Status != PromotionWaitingListStatus.Closed)
                .Select(l => l.Id!).Take(512).ToListAsync(cancellationToken);
            pending = await session.Query<PromotionBlock>().Where(b => b.Pending != null)
                .Select(b => b.Id!).Take(256).ToListAsync(cancellationToken);
            ending = await session.Query<PromotionBlock>().Where(b => b.ClosedAt == null && b.ClosesAt != null && b.ClosesAt <= now)
                .Select(b => b.Id!).Take(256).ToListAsync(cancellationToken);
            closed = await session.Query<PromotionBlock>().Where(b => b.ClosedAt != null && b.ClosedAt > since)
                .Select(b => b.Id!).Take(1024).ToListAsync(cancellationToken);
        }
        foreach (var id in pending)
            await SafeAsync(() => blocks.FinishPendingAsync(id, cancellationToken), id);
        foreach (var id in ending)
            await SafeAsync(() => blocks.CloseAsync(id, cancellationToken), id);
        foreach (var id in closed)
            await SafeAsync(() => blocks.ReturnSpareAsync(id, int.MaxValue, cancellationToken), id);
        foreach (var id in listIds)
            await SafeAsync(async () => { await waitingLists.EvaluateAsync(id, cancellationToken); return 0; }, id);
    }

    private async Task SafeAsync<T>(Func<Task<T>> work, string id)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Promotions reconciliation of {Id} failed.", id);
        }
    }
}

/// <summary>GB11: lapses old subscriptions and mails a batch of subscribers when an item is restocked.</summary>
internal sealed class BackInStockJob(BackInStock backInStock) : ISparkCronJob
{
    public static string CronSchedule => "0 * * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => backInStock.RunAsync(cancellationToken);
}
