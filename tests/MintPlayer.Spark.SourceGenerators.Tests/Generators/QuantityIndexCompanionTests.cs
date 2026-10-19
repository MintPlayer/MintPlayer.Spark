using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Warehouse WX8 / commerce X20 item 4: a <c>Quantity</c> property gets the same treatment as a <c>Money</c>
/// one (<see cref="MoneyIndexCompanionTests"/>): stored but not indexed, with flat <c>{Name}Amount</c> /
/// <c>{Name}Unit</c> companions the query executor sorts and filters through, and never a
/// <c>[ValueObject]</c> candidate (SPARK017).
/// </summary>
public class QuantityIndexCompanionTests
{
    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(
            "GenerateIndexGenerator",
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask), typeof(Quantity)],
            rootNamespace: "TestApp", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

    [Fact]
    public void A_quantity_property_is_stored_unindexed_with_amount_and_unit_companions()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class StockLine
            {
                public string? Id { get; set; }
                public string Sku { get; set; } = string.Empty;
                public Quantity? OnHand { get; set; }
            }
            """);
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("OnHand = stockLine.OnHand,");
        generated.Should().Contain("Index(nameof(VStockLine.OnHand), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
        generated.Should().Contain("OnHandAmount = stockLine.OnHand!.Amount,");
        generated.Should().Contain("OnHandUnit = stockLine.OnHand!.Unit,");
        generated.Should().Contain("public decimal? OnHandAmount");
        generated.Should().Contain("public string? OnHandUnit");
        generated.Should().NotContain("OnHandCurrency");
        // Not left as an unfilterable complex field.
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_INDEX_010");
    }

    [Fact]
    public void A_companion_name_the_entity_already_uses_is_not_generated_twice()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class StockLine
            {
                public string? Id { get; set; }
                public Quantity? OnHand { get; set; }
                public string? OnHandUnit { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("OnHandAmount = stockLine.OnHand!.Amount,");
        generated.Should().NotContain("OnHandUnit = stockLine.OnHand!.Unit,");
    }

    [Fact]
    public async Task Quantity_is_never_reported_as_a_missing_value_object()
    {
        var diagnostics = await GeneratorHarness.RunAnalyzerAsync("ValueObjectCompletenessAnalyzer",
        [
            """
            using System.Collections.Generic;
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Item> Items { get; set; } = null!;
            }

            public class Item
            {
                public string? Id { get; set; }
                public Quantity? OnHand { get; set; }
                public List<Quantity> Reservations { get; set; } = new();
            }
            """,
        ],
        [typeof(SparkContext), typeof(ValueObjectAttribute), typeof(Quantity), typeof(Raven.Client.Documents.Linq.IRavenQueryable<>)]);

        diagnostics.Should().NotContain(d => d.Id == "SPARK017");
    }
}
