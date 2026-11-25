using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>The suite against RavenDB's data subscriptions.</summary>
public sealed class RavenSubscriptionsConformanceTests : SparkSubscriptionsConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<(ISparkStore Store, ISparkSubscriptions Subscriptions)?> CreateAsync()
    {
        await driver.InitializeAsync();
        return (new RavenSparkStore(driver.DocumentStore), new RavenSparkSubscriptions(driver.DocumentStore));
    }

    public override async Task DisposeAsync() => await driver.DisposeAsync();

    private sealed class Driver : RavenSparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store's commit order.</summary>
public sealed class InMemorySubscriptionsConformanceTests : SparkSubscriptionsConformanceTests
{
    protected override Task<(ISparkStore Store, ISparkSubscriptions Subscriptions)?> CreateAsync()
    {
        var database = new InMemoryDatabase();
        var values = new InMemoryAtomicValues();
        return Task.FromResult<(ISparkStore, ISparkSubscriptions)?>(
            (new InMemorySparkStore(database), new InMemorySparkSubscriptions(database, values, new SparkStoreLock(values, TimeProvider.System))));
    }
}
