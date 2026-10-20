namespace MintPlayer.Spark.Warehouse.Tests;

/// <summary>PRD §4.13 "Startup refuses": invalid <c>Spark:Warehouse</c> values are named for the person editing appsettings.json.</summary>
public class OptionsValidationTests
{
    private static readonly string[] Rules = [WarehouseStrategyNames.Fifo, WarehouseStrategyNames.Fefo, WarehouseStrategyNames.ClosestLocation];

    [Fact]
    public void The_defaults_are_valid()
        => SparkWarehouseOptionsValidator.Problems(new SparkWarehouseOptions(), Rules).Should().BeEmpty();

    [Fact]
    public void An_unknown_allocation_rule_and_out_of_range_values_refuse_startup()
    {
        var options = new SparkWarehouseOptions();
        options.Allocation.Rule = "Cheapest";
        options.Stock.MaxAttempts = 0;
        options.Tasks.Lease = TimeSpan.FromSeconds(5);
        options.Locations.CheckDigitLength = 9;

        var problems = SparkWarehouseOptionsValidator.Problems(options, Rules).ToList();

        problems.Count.Should().Be(4);
        problems[0].Contains("Cheapest").Should().BeTrue();
    }
}
