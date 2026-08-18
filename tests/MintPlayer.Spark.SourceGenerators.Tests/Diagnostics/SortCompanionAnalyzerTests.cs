using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// The analyzer covers what generated code cannot: hand-written index pairs. Generated pairs are correct by
/// construction and excluded from analysis.
/// </summary>
public class SortCompanionAnalyzerTests
{
    private const string AnalyzerName = "SortCompanionAnalyzer";

    /// <summary>Stand-in for the RavenDB base class so fixtures compile without referencing RavenDB.</summary>
    private const string RavenStub = """
        namespace Raven.Client.Documents.Indexes;

        public enum FieldIndexing { Default, Search, Exact }

        public enum FieldStorage { No, Yes }

        public abstract class AbstractIndexCreationTask<T>
        {
            protected void Index(string field, FieldIndexing indexing) { }
            protected void StoreAllFields(FieldStorage storage) { }
            public object Map { get; set; } = null!;
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source, RavenStub],
            referenceTypes: [typeof(FromIndexAttribute)]);

    [Fact]
    public async Task A_searchable_field_without_a_companion_is_flagged()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Index(nameof(VCar.Model), FieldIndexing.Search);
                    StoreAllFields(FieldStorage.Yes);
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Model { get; set; }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK005");
    }

    [Fact]
    public async Task A_searchable_field_with_a_mapped_companion_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = new VCar { Model = null, ModelSort = null };
                    Index(nameof(VCar.Model), FieldIndexing.Search);
                    StoreAllFields(FieldStorage.Yes);
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Model { get; set; }
                [IgnoreProperty] public string? ModelSort { get; set; }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// The hazard of generating half a hand-written pair: the companion exists but nothing feeds it, so it
    /// indexes as null for every document while the index reports perfectly healthy.
    /// </summary>
    [Fact]
    public async Task A_companion_the_map_never_assigns_is_flagged()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = new VCar { Model = null };
                    Index(nameof(VCar.Model), FieldIndexing.Search);
                    StoreAllFields(FieldStorage.Yes);
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Model { get; set; }
                [IgnoreProperty] public string? ModelSort { get; set; }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK006");
    }

    [Fact]
    public async Task An_exact_indexed_field_also_needs_a_companion()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Code { get; set; } }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Index(nameof(VCar.Code), FieldIndexing.Exact);
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Code { get; set; }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK005");
    }

    [Fact]
    public async Task An_index_that_declares_no_analyzed_field_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    StoreAllFields(FieldStorage.Yes);
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Model { get; set; }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_class_without_FromIndex_is_ignored()
    {
        var diagnostics = await RunAsync("""
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class VCar
            {
                public string? Model { get; set; }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>A literal field name is as valid as <c>nameof</c> and must be understood too.</summary>
    [Fact]
    public async Task A_literal_field_name_is_understood()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Index("Model", FieldIndexing.Search);
                }
            }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Model { get; set; }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK005");
    }
}
