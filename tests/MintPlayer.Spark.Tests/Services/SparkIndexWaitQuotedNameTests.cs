using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// D-O19 (live test 2026-10-17): the request write path waits on every stale index over the collections a
/// write touched, through a <c>limit 0</c> query on the index. The RavenDB client writes that query as
/// <c>from index '{name}'</c> without escaping the name, and an auto index whose name holds a quote
/// (<c>Auto/Repositories/By'Connection'And…</c>) failed with "Expected end of query" — the wait for it was
/// skipped and a warning logged on every write to the collection. The name is now validated and quoted by
/// <see cref="SparkIndexWait"/> itself, never spliced unchecked.
/// </summary>
public class SparkIndexWaitQuotedNameTests : SparkTestDriver
{
    public class Thing
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public async Task An_index_whose_name_holds_a_quote_is_waited_on_without_a_parse_error()
    {
        await SeedAsync(session => session.StoreAsync(new Thing { Name = "alpha" }));

        // A dynamic query on a field the RQL must quote creates an auto index with the quote in its name.
        using (var session = Store.OpenAsyncSession())
        {
            await session.Advanced.AsyncRawQuery<Thing>("from Things where 'Odd Name' = $v")
                .AddParameter("v", "x")
                .WaitForNonStaleResults()
                .ToListAsync();
        }

        var autoIndex = (await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 64)))
            .Single(name => name.StartsWith("Auto/Things", StringComparison.Ordinal));
        autoIndex.Should().Contain("'", "the precondition: the auto index name holds a quote");

        // Stale while the wait starts: indexing paused, a new write, then resumed shortly after.
        await Store.Maintenance.SendAsync(new StopIndexOperation(autoIndex));
        using (var session = Store.OpenAsyncSession())
        {
            // Not SeedAsync: it waits for the indexes, and this one is paused on purpose.
            await session.StoreAsync(new Thing { Name = "beta" });
            await session.SaveChangesAsync();
        }
        var resume = Task.Run(async () =>
        {
            await Task.Delay(300);
            await Store.Maintenance.SendAsync(new StartIndexOperation(autoIndex));
        });

        var failures = new List<(Exception Error, string Index)>();
        var caughtUp = await SparkIndexWait.WaitAsync(
            Store, Store.Database, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Things" },
            TimeSpan.FromSeconds(15), (ex, index, _) => failures.Add((ex, index)), CancellationToken.None);
        await resume;

        failures.Should().BeEmpty();
        caughtUp.Should().BeTrue();
        (await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation(autoIndex))).IsStale
            .Should().BeFalse("the wait returned only once the index had the write");
    }

    [Theory]
    [InlineData("Auto/Repositories/By'Connection'AndAccount", true)]
    [InlineData("Repositories_Overview", true)]
    [InlineData("Auto/Things/ByKnownChecks[].IdAndSearch(Name)", true)]
    [InlineData("Evil\" limit 0 update { this.x = 1 } //", false)]
    [InlineData("Back\\slash", false)]
    [InlineData("New\nLine", false)]
    [InlineData("", false)]
    public void Only_a_name_that_cannot_leave_its_quotes_is_quoted(string name, bool quotable)
    {
        var quote = () => SparkIndexWait.QuoteIndexName(name);
        if (quotable)
            quote().Should().Be($"\"{name}\"");
        else
            quote.Should().Throw<ArgumentException>();
    }
}
