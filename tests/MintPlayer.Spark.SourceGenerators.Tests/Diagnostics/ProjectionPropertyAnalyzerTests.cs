using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

public class ProjectionPropertyAnalyzerTests
{
    private const string AnalyzerName = "ProjectionPropertyAnalyzer";

    private static IEnumerable<Type> DefaultRefs =>
    [
        typeof(FromIndexAttribute),
        typeof(ReferenceAttribute),
        typeof(AbstractIndexCreationTask<>),
    ];

    [Fact]
    public async Task Projection_whose_property_type_matches_entity_has_no_diagnostics()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string Plate { get; set; } = ""; }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new { c.Plate };
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string Plate { get; set; } = "";
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignored_property_raises_no_diagnostic_even_when_the_types_mismatch()
    {
        // [IgnoreProperty] (#254) takes the property out of the model, so there is nothing left
        // to hold the entity and projection to agree on.
        var source = """
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car
            {
                public string Plate { get; set; } = "";
                [IgnoreProperty]
                public string Scratch { get; set; } = "";
            }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview() { Map = cars => from c in cars select new { c.Plate }; }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string Plate { get; set; } = "";
                public int Scratch { get; set; } // type mismatch, but the property is ignored
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Projection_with_mismatched_property_type_raises_SPARK001()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string Plate { get; set; } = ""; }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview() { Map = cars => from c in cars select new { c.Plate }; }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public int Plate { get; set; } // should be string
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK001");
    }

    [Fact]
    public async Task Projection_missing_Reference_attribute_raises_SPARK002()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Brand { public string? Id { get; set; } }

            public class Car
            {
                [Reference(typeof(Brand))]
                public string BrandId { get; set; } = "";
            }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview() { Map = cars => from c in cars select new { c.BrandId }; }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string BrandId { get; set; } = ""; // [Reference] missing
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK002");
    }

    [Fact]
    public async Task Projection_without_FromIndex_attribute_is_not_analyzed()
    {
        var source = """
            namespace TestApp;

            public class VCar
            {
                public int SomethingWeird { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty();
    }
}
