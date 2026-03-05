using System.Runtime.CompilerServices;
using DemoApp.Library.Entities;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;

namespace DemoApp.Actions;

public partial class StockActions : DefaultPersistentObjectActions<Stock>
{
    public async IAsyncEnumerable<IReadOnlyList<Stock>> StreamStocks(
        StreamingQueryArgs args,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var random = new Random();

        // Seed initial stock data
        var stocks = new List<Stock>
        {
            new() { Id = "stocks/AAPL", Symbol = "AAPL", CompanyName = "Apple Inc.", CurrentPrice = 189.50m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/MSFT", Symbol = "MSFT", CompanyName = "Microsoft Corp.", CurrentPrice = 415.20m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/GOOG", Symbol = "GOOG", CompanyName = "Alphabet Inc.", CurrentPrice = 141.80m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/AMZN", Symbol = "AMZN", CompanyName = "Amazon.com Inc.", CurrentPrice = 178.30m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/TSLA", Symbol = "TSLA", CompanyName = "Tesla Inc.", CurrentPrice = 248.90m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/META", Symbol = "META", CompanyName = "Meta Platforms Inc.", CurrentPrice = 505.60m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/NVDA", Symbol = "NVDA", CompanyName = "NVIDIA Corp.", CurrentPrice = 875.40m, Change = 0m, ChangePercent = 0m },
            new() { Id = "stocks/NFLX", Symbol = "NFLX", CompanyName = "Netflix Inc.", CurrentPrice = 628.70m, Change = 0m, ChangePercent = 0m },
        };

        var basePrices = stocks.ToDictionary(s => s.Id!, s => s.CurrentPrice);

        // Yield initial snapshot
        yield return stocks;

        // Continuously generate price updates
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);

            // Randomly update 2-4 stocks
            var updateCount = random.Next(2, 5);
            var indicesToUpdate = Enumerable.Range(0, stocks.Count)
                .OrderBy(_ => random.Next())
                .Take(updateCount);

            foreach (var i in indicesToUpdate)
            {
                var stock = stocks[i];
                var basePrice = basePrices[stock.Id!];

                // Random price change: -2% to +2%
                var changePct = (decimal)(random.NextDouble() * 4 - 2);
                var priceChange = Math.Round(basePrice * changePct / 100m, 2);
                var newPrice = Math.Round(basePrice + priceChange, 2);
                if (newPrice < 1m) newPrice = 1m;

                stock.CurrentPrice = newPrice;
                stock.Change = Math.Round(newPrice - basePrices[stock.Id!], 2);
                stock.ChangePercent = Math.Round((newPrice - basePrices[stock.Id!]) / basePrices[stock.Id!] * 100m, 2);
            }

            yield return stocks;
        }
    }
}
