using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Commerce X4: a <c>Money</c> property is an object RavenDB cannot sort or compare, so a generated index
/// keeps it stored but not indexed and adds the flat <c>{Name}Amount</c> / <c>{Name}Currency</c> companions
/// the query executor sorts and filters through. And Money is one value, never a <c>[ValueObject]</c>
/// candidate (SPARK017).
/// </summary>
public class MoneyIndexCompanionTests
{
    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(
            "GenerateIndexGenerator",
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask), typeof(Money)],
            rootNamespace: "TestApp", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

    private const string Product = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Product
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public Money? Price { get; set; }
        }
        """;

    [Fact]
    public void A_money_property_is_stored_unindexed_with_amount_and_currency_companions()
    {
        var result = Run(Product);
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("Price = product.Price,");
        generated.Should().Contain("Index(nameof(VProduct.Price), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
        generated.Should().Contain("PriceAmount = product.Price!.Amount,");
        generated.Should().Contain("PriceCurrency = product.Price!.Currency,");
        generated.Should().Contain("public decimal? PriceAmount");
        generated.Should().Contain("public string? PriceCurrency");
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
            public class Product
            {
                public string? Id { get; set; }
                public Money? Price { get; set; }
                public string? PriceCurrency { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("PriceAmount = product.Price!.Amount,");
        generated.Should().NotContain("PriceCurrency = product.Price!.Currency,");
    }

    [Fact]
    public async Task Money_is_never_reported_as_a_missing_value_object()
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
                public IRavenQueryable<Order> Orders { get; set; } = null!;
            }

            public class Order
            {
                public string? Id { get; set; }
                public Money? Total { get; set; }
                public List<Money> Instalments { get; set; } = new();
            }
            """,
        ],
        [typeof(SparkContext), typeof(ValueObjectAttribute), typeof(Money), typeof(Raven.Client.Documents.Linq.IRavenQueryable<>)]);

        diagnostics.Should().NotContain(d => d.Id == "SPARK017");
    }
}
