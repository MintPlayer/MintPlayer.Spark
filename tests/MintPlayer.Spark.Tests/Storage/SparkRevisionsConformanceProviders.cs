using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>The suite against RavenDB's own revisions.</summary>
public sealed class RavenRevisionsConformanceTests : SparkRevisionsConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<(ISparkStore Store, ISparkRevisions Revisions)?> CreateAsync()
    {
        await driver.InitializeAsync();
        return (new RavenSparkStore(driver.DocumentStore), new RavenSparkRevisions(driver.DocumentStore, NullLogger<RavenSparkRevisions>.Instance));
    }

    public override async Task DisposeAsync() => await driver.DisposeAsync();

    private sealed class Driver : RavenSparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store's revisions.</summary>
public sealed class InMemoryRevisionsConformanceTests : SparkRevisionsConformanceTests
{
    protected override Task<(ISparkStore Store, ISparkRevisions Revisions)?> CreateAsync()
    {
        var database = new InMemoryDatabase();
        return Task.FromResult<(ISparkStore, ISparkRevisions)?>((new InMemorySparkStore(database), new InMemorySparkRevisions(database)));
    }
}
