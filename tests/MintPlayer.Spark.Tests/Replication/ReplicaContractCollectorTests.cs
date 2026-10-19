using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// The consumer side of X20 item 7: a <c>[Replicated]</c> replica type yields only
/// <c>(contract, version, collection)</c>. Nothing a consumer collects is code the owner would run (F16).
/// </summary>
public class ReplicaContractCollectorTests
{
    private static readonly Func<Type, string> CollectionOf = type => type.Name + "Collection";

    [Fact]
    public void Collects_classes_decorated_with_Replicated_and_groups_them_by_SourceModule()
    {
        var result = new ReplicaContractCollector().Collect(CollectionOf, typeof(ReplicaContractCollectorTests).Assembly);

        result.Should().ContainKey("Fleet");
        result.Should().ContainKey("HR");
        result["Fleet"].Should().HaveCount(2, "ReplicatedCarFromFleet + ReplicatedDriverFromFleet");
        result["HR"].Should().HaveCount(1, "ReplicatedEmployeeFromHR");
    }

    [Fact]
    public void A_replica_names_its_contract_version_and_its_own_collection()
    {
        var result = new ReplicaContractCollector().Collect(CollectionOf, typeof(ReplicaContractCollectorTests).Assembly);

        var driver = result["Fleet"].Single(c => c.Contract == "drivers");
        driver.Version.Should().Be(2);
        driver.TargetCollection.Should().Be(nameof(ReplicatedDriverFromFleet) + "Collection",
            "the target is the consumer's collection for the replica type, from the consumer store's conventions");
    }

    [Fact]
    public void The_version_defaults_to_1()
    {
        var result = new ReplicaContractCollector().Collect(CollectionOf, typeof(ReplicaContractCollectorTests).Assembly);

        result["Fleet"].Single(c => c.Contract == "cars").Version.Should().Be(1);
    }

    /// <summary>
    /// F16 by construction: what travels to the owner has no member that could carry a script.
    /// </summary>
    [Fact]
    public void A_contract_request_carries_no_script()
    {
        typeof(MintPlayer.Spark.Replication.Abstractions.Models.EtlContractRequest).GetProperties()
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["Contract", "Version", "TargetCollection"]);
        typeof(ReplicatedAttribute).GetProperty("EtlScript").Should().BeNull();
    }

    [Fact]
    public void Returns_empty_dictionary_when_no_assembly_contains_Replicated_types()
    {
        var result = new ReplicaContractCollector().Collect(CollectionOf, typeof(string).Assembly);

        result.Should().BeEmpty();
    }

    // --- Fixtures (must be public to satisfy GetExportedTypes) ---

    public class Employee
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Car
    {
        public string? Id { get; set; }
        public string Plate { get; set; } = "";
    }
}

[Replicated(
    SourceModule = "Fleet",
    Contract = "cars",
    OriginalType = typeof(ReplicaContractCollectorTests.Car))]
public class ReplicatedCarFromFleet
{
    public string? Id { get; set; }
    public string Plate { get; set; } = "";

    // #254 — must not cross the module boundary, in the data or the writable-property list.
    // The Properties assertions elsewhere in these tests double as proof of that.
    [IgnoreProperty]
    public string InternalToken { get; set; } = "";

    // #253 — get-only, so it appears in the model as a read-only attribute but must never reach
    // the write-authorization list. Every `Properties.Should().Equal("Plate")` assertion in these
    // tests doubles as proof of that.
    public string Display => $"{Plate} (car)";
}

[Replicated(SourceModule = "Fleet", Contract = "drivers", Version = 2, SourceCollection = "VehicleDrivers")]
public class ReplicatedDriverFromFleet
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

[Replicated(SourceModule = "HR", Contract = "employees", OriginalType = typeof(ReplicaContractCollectorTests.Employee))]
public class ReplicatedEmployeeFromHR
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}
