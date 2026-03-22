using DemoApp.Library.Entities;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using System.Runtime.CompilerServices;

namespace DemoApp.Actions;

public partial class StockActions : DefaultPersistentObjectActions<Stock>
{
    public override async IAsyncEnumerable<IReadOnlyList<Stock>> StreamItems(
        StreamingQueryArgs args,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var random = new Random();

        // Seed initial stock data — well-known tickers + generated ones to reach 300
        var knownStocks = new (string Symbol, string Company, decimal Price)[]
        {
            ("AAPL", "Apple Inc.", 189.50m), ("MSFT", "Microsoft Corp.", 415.20m),
            ("GOOG", "Alphabet Inc.", 141.80m), ("AMZN", "Amazon.com Inc.", 178.30m),
            ("TSLA", "Tesla Inc.", 248.90m), ("META", "Meta Platforms Inc.", 505.60m),
            ("NVDA", "NVIDIA Corp.", 875.40m), ("NFLX", "Netflix Inc.", 628.70m),
            ("JPM", "JPMorgan Chase & Co.", 198.40m), ("V", "Visa Inc.", 279.30m),
            ("WMT", "Walmart Inc.", 168.20m), ("JNJ", "Johnson & Johnson", 155.80m),
            ("PG", "Procter & Gamble Co.", 162.50m), ("MA", "Mastercard Inc.", 458.90m),
            ("HD", "Home Depot Inc.", 362.70m), ("UNH", "UnitedHealth Group Inc.", 527.30m),
            ("DIS", "Walt Disney Co.", 112.40m), ("BAC", "Bank of America Corp.", 35.20m),
            ("ADBE", "Adobe Inc.", 552.60m), ("CRM", "Salesforce Inc.", 265.40m),
            ("CSCO", "Cisco Systems Inc.", 50.80m), ("PFE", "Pfizer Inc.", 27.30m),
            ("INTC", "Intel Corp.", 43.60m), ("AMD", "Advanced Micro Devices Inc.", 164.20m),
            ("ORCL", "Oracle Corp.", 125.80m), ("PYPL", "PayPal Holdings Inc.", 63.50m),
            ("QCOM", "Qualcomm Inc.", 168.90m),
            ("UBER", "Uber Technologies Inc.", 72.40m), ("SPOT", "Spotify Technology SA", 248.30m),
        };

        var stocks = new List<Stock>(300);
        foreach (var (symbol, company, price) in knownStocks)
        {
            stocks.Add(new() { Id = $"stocks/{symbol}", Symbol = symbol, CompanyName = company, CurrentPrice = price, Change = 0m, ChangePercent = 0m });
        }

        // Generate remaining stocks to reach 300
        for (var i = stocks.Count; i < 300; i++)
        {
            var symbol = $"STK{i:D3}";
            var price = Math.Round((decimal)(random.Next(5, 800) + random.NextDouble()), 2);
            stocks.Add(new() { Id = $"stocks/{symbol}", Symbol = symbol, CompanyName = $"Company {symbol}", CurrentPrice = price, Change = 0m, ChangePercent = 0m });
        }

        var basePrices = stocks.ToDictionary(s => s.Id!, s => s.CurrentPrice);

        // Yield initial snapshot
        yield return stocks;

        // Continuously generate price updates
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);

            // Randomly update 5-15 stocks per tick
            var updateCount = random.Next(5, 16);
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
