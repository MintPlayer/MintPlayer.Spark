using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// #273 — SPARK007/SPARK008, the local placement rules for the property-level [Breadcrumb] marker.
/// The generator's SPARK_INDEX_012 covers the same misuses in the app compilation; these fire in
/// the entity library itself, where the author is typing.
/// </summary>
public class BreadcrumbPlacementAnalyzerTests
{
    private const string AnalyzerName = "BreadcrumbPlacementAnalyzer";

    private static IEnumerable<Type> DefaultRefs =>
    [
        typeof(BreadcrumbAttribute),
        typeof(IgnorePropertyAttribute),
    ];

    [Fact]
    public async Task Marker_inside_a_FromIndex_projection_raises_SPARK007()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Cars_Overview { }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Id { get; set; }
                [Breadcrumb] public string Model { get; set; } = "";
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK007");
        diagnostics[0].GetMessage().Should().Contain("VCar").And.Contain("Model");
    }

    [Fact]
    public async Task Marker_on_a_Reference_id_raises_SPARK008()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Profession { }

            public class Job
            {
                [Breadcrumb, Reference(typeof(Profession))]
                public string? ProfessionId { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK008");
        diagnostics[0].GetMessage().Should().Contain("ProfessionId");
    }

    [Fact]
    public async Task Marker_on_a_collection_raises_SPARK008()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;

            namespace TestApp;

            public class Address
            {
                [Breadcrumb] public List<string> Lines { get; set; } = new();
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK008");
    }

    [Fact]
    public async Task Marker_on_a_TranslatedString_raises_SPARK008()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Product
            {
                [Breadcrumb] public TranslatedString? Description { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK008");
    }

    [Fact]
    public async Task Marker_on_a_scalar_or_computed_property_of_a_plain_class_is_clean()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Address
            {
                public string City { get; set; } = "";
                public string Street { get; set; } = "";

                [Breadcrumb, IgnoreProperty]
                public string Crumb => $"{Street}, {City}";
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty("the sanctioned marker shapes must not be diagnosed");
    }

    [Fact]
    public async Task Marker_on_a_complex_typed_property_is_clean_here()
    {
        // Delegation to the nested type's own marker is a cross-type judgment the generator makes
        // (SPARK_INDEX_012 when the chain dead-ends); the local analyzer stays silent.
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class AddressDescription
            {
                [Breadcrumb] public string Title { get; set; } = "";
            }

            public class Address
            {
                [Breadcrumb] public AddressDescription? Description { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty();
    }
}
