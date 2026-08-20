using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK009: one <c>[DefaultIndex]</c> per collection type per compilation. The catalog's freeze-time
/// validation stays authoritative across assemblies; this mirrors it at compile time.
/// </summary>
public class DefaultIndexAnalyzerTests
{
    private const string AnalyzerName = "DefaultIndexAnalyzer";

    /// <summary>Stand-in for the RavenDB base classes so fixtures compile without referencing RavenDB.</summary>
    private const string RavenStub = """
        namespace Raven.Client.Documents.Indexes;

        public abstract class AbstractIndexCreationTask<T>
        {
        }

        public abstract class AbstractIndexCreationTask<TDocument, TReduceResult> : AbstractIndexCreationTask<TDocument>
        {
        }

        public abstract class AbstractMultiMapIndexCreationTask<T>
        {
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source, RavenStub],
            referenceTypes: [typeof(DefaultIndexAttribute)]);

    [Fact]
    public async Task Two_marked_indexes_over_one_collection_are_flagged_on_both()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
            }

            [DefaultIndex]
            public class Cars_Search : AbstractIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_single_marked_index_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
            }

            public class Cars_Search : AbstractIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Marked_indexes_over_different_collections_are_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }
            public class Person { public string? Name { get; set; } }

            [DefaultIndex]
            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
            }

            [DefaultIndex]
            public class People_Overview : AbstractIndexCreationTask<Person>
            {
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_marked_map_reduce_index_clashes_with_a_marked_plain_index()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }
            public class CarCount { public int Count { get; set; } }

            [DefaultIndex]
            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
            }

            [DefaultIndex]
            public class Cars_ByCount : AbstractIndexCreationTask<Car, CarCount>
            {
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_marked_multi_map_index_clashes_with_a_marked_plain_index()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
            }

            [DefaultIndex]
            public class Cars_MultiMap : AbstractMultiMapIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2);
    }

    [Fact]
    public async Task An_abstract_marked_base_is_ignored()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public abstract class Cars_Base : AbstractIndexCreationTask<Car>
            {
            }

            [DefaultIndex]
            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// A [GenerateIndex] entity claims the default through its generated index, so a hand-written
    /// [DefaultIndex] over the same entity clashes with the entity itself — the analyzer must not
    /// depend on the generated tree being analyzed (these fixtures never run the generator).
    /// </summary>
    [Fact]
    public async Task A_GenerateIndex_entity_clashes_with_a_hand_marked_index()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            [GenerateIndex]
            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public class Cars_Search : AbstractIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_GenerateIndex_entity_opted_out_with_IsDefault_false_is_clean_beside_a_hand_marked_index()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            [GenerateIndex(IsDefault = false)]
            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public class Cars_Search : AbstractIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_GenerateIndex_entity_alone_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [GenerateIndex]
            public class Car { public string? Model { get; set; } }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task The_generated_index_name_override_is_used_in_the_clash_message()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            [GenerateIndex(IndexName = "Cars_Custom")]
            public class Car { public string? Model { get; set; } }

            [DefaultIndex]
            public class Cars_Search : AbstractIndexCreationTask<Car>
            {
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2)
            .And.Contain(d => d.GetMessage(null).Contains("Cars_Custom"));
    }

    [Fact]
    public async Task A_marked_non_index_class_is_ignored()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [DefaultIndex]
            public class NotAnIndex
            {
            }
            """);

        diagnostics.Should().BeEmpty();
    }
}
