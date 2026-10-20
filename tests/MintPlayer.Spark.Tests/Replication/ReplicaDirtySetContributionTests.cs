using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// A library contributes dirty-set collections (lead decision 2026-10-20, warehouse <c>FulfilmentStockLevels</c>). Red
/// before: the flush job was registered only when <c>Spark:Replication:DirtySetCollections</c> was non-empty at
/// <c>AddReplication</c> time, so a collection a library added was marked dirty and never flushed.
/// </summary>
public class ReplicaDirtySetContributionTests
{
    private static SparkBuilder NewBuilder() => new(new ServiceCollection());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_library_collection_is_in_the_options_and_gets_the_flush_job_whatever_the_order(bool libraryFirst)
    {
        var builder = NewBuilder();
        if (libraryFirst)
            builder.AddReplicaDirtySetCollection("FulfilmentStockLevels");
        builder.AddReplication(o => { o.ModuleName = "Warehouse"; o.ModuleUrl = "https://warehouse.test"; });
        if (!libraryFirst)
            builder.AddReplicaDirtySetCollection("FulfilmentStockLevels");

        builder.Services.Count(d => d.ServiceType == typeof(ReplicaDirtySetFlushJob)).Should().Be(1);
        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<SparkReplicationOptions>>().Value.DirtySetCollections.Contains("FulfilmentStockLevels").Should().BeTrue();
    }

    [Fact]
    public void The_hosts_own_collections_stay_beside_the_librarys()
    {
        var builder = NewBuilder();
        builder.AddReplication(o =>
        {
            o.ModuleName = "Warehouse";
            o.ModuleUrl = "https://warehouse.test";
            o.DirtySetCollections.Add("HostCounters");
        });
        builder.AddReplicaDirtySetCollection("FulfilmentStockLevels");

        builder.Services.Count(d => d.ServiceType == typeof(ReplicaDirtySetFlushJob)).Should().Be(1);
        using var provider = builder.Services.BuildServiceProvider();
        var collections = provider.GetRequiredService<IOptions<SparkReplicationOptions>>().Value.DirtySetCollections;
        collections.Contains("HostCounters").Should().BeTrue();
        collections.Contains("FulfilmentStockLevels").Should().BeTrue();
    }

    [Fact]
    public void A_collection_name_must_be_an_identifier()
    {
        var act = () => NewBuilder().AddReplicaDirtySetCollection("Fulfilment/StockLevels");
        act.Should().Throw<ArgumentException>();
    }
}
