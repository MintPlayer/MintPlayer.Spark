using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

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

    public override async Task DisposeAsync() => await driver.DisposeAsync();

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
