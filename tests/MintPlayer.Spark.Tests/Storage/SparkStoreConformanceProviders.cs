using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>The suite against RavenDB, an embedded server per class.</summary>
public sealed class RavenSparkStoreConformanceTests : SparkStoreConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<ISparkStore?> CreateStoreAsync()
    {
        await driver.InitializeAsync();
        return new RavenSparkStore(driver.DocumentStore);
    }

    protected override IQueryable<T> Settled<T>(IQueryable<T> query)
        => ((IRavenQueryable<T>)query).Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)));

    /// <summary>RavenDB's LINQ refuses <c>string.Contains</c> ("use Search()"); its auto-indexes match terms, not substrings.</summary>
    protected override bool QueriesSubstrings => false;

    public override async Task DisposeAsync() => await driver.DisposeAsync();

    /// <summary>A patch that deletes the property, so the document is the shape an older entity wrote.</summary>
    protected override async Task RemoveFieldAsync(string id, string field)
    {
        var operation = await driver.DocumentStore.Operations.SendAsync(new Raven.Client.Documents.Operations.PatchByQueryOperation(
            new Raven.Client.Documents.Queries.IndexQuery
            {
                Query = $"from ConfDocs as d where id() = $id update {{ delete d.{MintPlayer.Spark.Abstractions.RqlIdentifier.FieldPath(field)}; }}",
                QueryParameters = new Raven.Client.Parameters { ["id"] = id },
            }));
        await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
    }

    public class ConfDocs_ByName : Raven.Client.Documents.Indexes.AbstractIndexCreationTask<ConfDoc>
    {
        public ConfDocs_ByName()
        {
            Map = docs => from d in docs select new { d.Name };
        }
    }

    /// <summary>
    /// An index-backed root (<c>Session.Query&lt;T, TIndex&gt;()</c>, what every generated view root calls). Its binding used
    /// to be an open-instance delegate over a generic interface method, which the runtime refuses with
    /// NotSupportedException, so each index root failed on first use.
    /// </summary>
    [Fact]
    public async Task An_index_query_reads_through_the_index()
    {
        await new ConfDocs_ByName().ExecuteAsync(driver.DocumentStore);
        using (var write = Store.OpenSession())
        {
            await write.StoreAsync(new ConfDoc { Id = "ConfDocs/indexed-1", Name = "Indexed" });
            await write.StoreAsync(new ConfDoc { Id = "ConfDocs/indexed-2", Name = "Other" });
            await write.SaveChangesAsync();
        }

        using var session = Store.OpenSession();
        var found = await session.ToListAsync(Settled(session.Query<ConfDoc, ConfDocs_ByName>()).Where(d => d.Name == "Indexed"));

        found.Select(d => d.Id).Should().Equal(["ConfDocs/indexed-1"]);
    }

    /// <summary>
    /// RavenDB claims a reservation before it writes the documents: compare-exchange values are not in the batch of a
    /// session that is not cluster-wide. A second claimant that arrives in between finds a holder whose document does not
    /// exist yet. Taking the key over then would save both documents and leave the key to the second.
    /// </summary>
    [Fact]
    public async Task A_claim_whose_save_is_still_in_flight_is_not_taken_over()
    {
        var raced = false;
        Exception? second = null;
        void Interleave(object? sender, BeforeStoreEventArgs e)
        {
            if (raced || !string.Equals(e.DocumentId, "ConfDocs/first", StringComparison.OrdinalIgnoreCase))
                return;
            raced = true;
            // Between the first session's claim and its document write: the second claims the same key and saves.
            second = Task.Run(() => Record.ExceptionAsync(async () =>
            {
                using var session = Store.OpenSession();
                await session.StoreAsync(new ConfDoc { Id = "ConfDocs/second", Name = "Second" });
                session.Reserve("conf-unique/race", "ConfDocs/second");
                await session.SaveChangesAsync();
            })).GetAwaiter().GetResult();
        }

        driver.DocumentStore.OnBeforeStore += Interleave;
        try
        {
            using var first = Store.OpenSession();
            await first.StoreAsync(new ConfDoc { Id = "ConfDocs/first", Name = "First" });
            first.Reserve("conf-unique/race", "ConfDocs/first");
            await first.SaveChangesAsync();
        }
        finally
        {
            driver.DocumentStore.OnBeforeStore -= Interleave;
        }

        raced.Should().BeTrue();
        second.Should().BeOfType<SparkReservationTakenException>("the holder's document is being written");
        using var read = Store.OpenSession();
        Canon(await read.GetReservationAsync("conf-unique/race")).Should().Be("confdocs/first");
        (await read.LoadAsync<ConfDoc>("ConfDocs/second")).Should().BeNull("a refused reservation writes nothing");
    }

    private sealed class Driver : RavenSparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store.</summary>
public sealed class InMemorySparkStoreConformanceTests : SparkStoreConformanceTests
{
    protected override Task<ISparkStore?> CreateStoreAsync()
        => Task.FromResult<ISparkStore?>(new InMemorySparkStore(new InMemoryDatabase()));
}
