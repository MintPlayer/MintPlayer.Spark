using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// A generic contract library (warehouse PRD §4.3, <c>[assembly: SparkReplicaContracts]</c>): a host consumes only the
/// contracts it binds in <c>Spark:Replication:Sources</c>; the others are inert, so an owner can reference the
/// library without its own contracts refusing startup.
/// </summary>
public class ReplicaContractLibraryTests
{
    private static readonly Type[] AllContracts =
    [
        typeof(FulfilmentOrder), typeof(FulfilmentCatalogItem), typeof(FulfilmentExpectedReturn), typeof(FulfilmentExpectedReceipt),
        typeof(FulfilmentOrderStatus), typeof(FulfilmentShipment), typeof(FulfilmentStockLevel), typeof(FulfilmentReturnReceipt),
    ];

    private static SparkReplicationOptions Options() => new() { ModuleName = "Warehouse", ModuleUrl = "https://localhost:5099" };

    [Fact]
    public void An_unbound_contract_is_inert()
    {
        var catalog = new ReplicaCatalog(Options(), AllContracts);

        catalog.All.Should().BeEmpty();
        catalog.IsReplica(typeof(FulfilmentShipment)).Should().BeFalse();
        catalog.Owners.Should().BeEmpty();
    }

    [Fact]
    public void A_warehouse_binds_the_shop_contracts_and_owns_the_rest()
    {
        var options = Options();
        options.Sources[FulfilmentCollections.Orders] = ["ShopA", "ShopB"];
        options.Sources[FulfilmentCollections.CatalogItems] = ["ShopA"];

        var catalog = new ReplicaCatalog(options, AllContracts);

        catalog.All.Select(d => d.TargetCollection).Should().BeEquivalentTo(new[] { FulfilmentCollections.Orders, FulfilmentCollections.CatalogItems });
        catalog.IsReplica(typeof(FulfilmentOrder)).Should().BeTrue();
        // The warehouse's own documents stay writable.
        catalog.IsReplica(typeof(FulfilmentShipment)).Should().BeFalse();
        catalog.FromOwner("ShopB").Select(d => d.Type).Should().BeEquivalentTo(new[] { typeof(FulfilmentOrder) });
    }

    [Fact]
    public void Every_contract_reads_and_writes_its_own_collection()
    {
        // Owner and consumer store the same class, so the generated source and target collections agree.
        foreach (var type in AllContracts)
        {
            var descriptor = ReplicaCatalog.Describe(type, Options());
            descriptor.TargetCollection.Should().Be(descriptor.SourceCollection);
            descriptor.WriteBack.Should().BeFalse();
        }
    }
}
