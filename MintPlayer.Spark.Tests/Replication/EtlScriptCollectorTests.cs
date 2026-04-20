using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Tests.Replication;

public class EtlScriptCollectorTests
{
    [Fact]
    public void Collects_classes_decorated_with_Replicated_and_groups_them_by_SourceModule()
    {
        var collector = new EtlScriptCollector();

        var result = collector.CollectScripts(typeof(EtlScriptCollectorTests).Assembly);

        result.Should().ContainKey("Fleet");
        result.Should().ContainKey("HR");
        result["Fleet"].Should().HaveCount(2, "ReplicatedCarFromFleet + ReplicatedDriverFromFleet");
        result["HR"].Should().HaveCount(1, "ReplicatedEmployeeFromHR");
    }

    [Fact]
    public void Source_collection_is_taken_from_the_attribute_when_set()
    {
        var collector = new EtlScriptCollector();

        var result = collector.CollectScripts(typeof(EtlScriptCollectorTests).Assembly);

        var driverItem = result["Fleet"].Single(s => s.Script.Contains("loadToDrivers"));
        driverItem.SourceCollection.Should().Be("VehicleDrivers");
    }

    [Fact]
    public void Source_collection_is_inferred_from_the_OriginalType_when_the_attribute_omits_it()
    {
        var collector = new EtlScriptCollector();

        var result = collector.CollectScripts(typeof(EtlScriptCollectorTests).Assembly);

        // [Replicated] without SourceCollection on ReplicatedEmployeeFromHR with OriginalType = typeof(Employee)
        var employeeItem = result["HR"].Single();
        employeeItem.SourceCollection.Should().Be("Employees", "pluralized from OriginalType = Employee");
    }

    [Fact]
    public void Pluralization_handles_y_to_ies_s_or_x_or_sh_or_ch_to_es_and_default_append_s()
    {
        var collector = new EtlScriptCollector();

        var result = collector.CollectScripts(typeof(EtlScriptCollectorTests).Assembly);

        // ReplicatedCarFromFleet → inferred from the class name itself (Car → Cars)
        var carItem = result["Fleet"].Single(s => s.Script.Contains("loadToCars"));
        carItem.SourceCollection.Should().Be("Cars");
    }

    [Fact]
    public void Returns_empty_dictionary_when_no_assembly_contains_Replicated_types()
    {
        var collector = new EtlScriptCollector();

        var result = collector.CollectScripts(typeof(string).Assembly);

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
    OriginalType = typeof(EtlScriptCollectorTests.Car),
    EtlScript = "loadToCars({ Plate: this.Plate });")]
public class ReplicatedCarFromFleet
{
    public string? Id { get; set; }
    public string Plate { get; set; } = "";
}

[Replicated(SourceModule = "Fleet", SourceCollection = "VehicleDrivers", EtlScript = "loadToDrivers({ Name: this.Name });")]
public class ReplicatedDriverFromFleet
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

[Replicated(SourceModule = "HR", OriginalType = typeof(EtlScriptCollectorTests.Employee), EtlScript = "loadToEmployees({ Name: this.Name });")]
public class ReplicatedEmployeeFromHR
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}
