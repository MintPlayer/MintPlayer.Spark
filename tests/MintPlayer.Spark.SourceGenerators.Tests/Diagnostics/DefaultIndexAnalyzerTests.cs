using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK009: one <c>[DefaultIndex]</c> per collection type per compilation. The catalog's freeze-time
/// validation stays authoritative across assemblies; this mirrors it at compile time.
/// </summary>
public class DefaultIndexAnalyzerTests
{
    private const string AnalyzerName = "DefaultIndexAnalyzer";

    /// <summary>
    /// The real RavenDB types, not a stand-in.
    /// </summary>
    /// <remarks>
    /// ⚠️ A hand-written stub used to stand in here, and it declared
    /// <c>AbstractIndexCreationTask&lt;TDocument, TReduceResult&gt; : AbstractIndexCreationTask&lt;TDocument&gt;</c>
    /// — <b>a hierarchy RavenDB does not have</b>. The real two-argument form derives from
    /// <c>AbstractGenericIndexCreationTask&lt;TReduceResult&gt;</c>, and the one-argument form derives
    /// <em>from the two-argument one</em>. So the map-reduce test below passed only because the fixture
    /// agreed with the analyzer's mistake rather than with RavenDB, and proved nothing.
    /// <para>
    /// The test project already references <c>MintPlayer.Spark</c> and therefore RavenDB.Client, and
    /// the sibling <c>ProjectionPropertyAnalyzerTests</c> already passes the real
    /// <c>typeof(AbstractIndexCreationTask&lt;&gt;)</c>. Using the real types removes the whole class of
    /// "our stub disagrees with reality" defect instead of re-spelling the stub correctly.
    /// </para>
    /// </remarks>
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source],
            referenceTypes: [typeof(DefaultIndexAttribute), typeof(AbstractIndexCreationTask<>)]);

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

    /// <summary>
    /// A multi-map index claims no collection, so it cannot contend for one collection's default.
    /// </summary>
    /// <remarks>
    /// ⚠️ This test used to assert the opposite — two SPARK009 diagnostics — and its fixture spelled
    /// out why: <c>Cars_MultiMap : AbstractMultiMapIndexCreationTask&lt;Car&gt;</c>, read as "a
    /// multi-map over Car". That reading is wrong. The single type argument of
    /// <c>AbstractMultiMapIndexCreationTask&lt;T&gt;</c> is the <b>reduce result</b>; a multi-map maps
    /// several collections and names none of them. So the premise of the old test did not hold and it
    /// could only ever have passed against the fictional stub.
    /// </remarks>
    [Fact]
    public async Task A_marked_multi_map_index_does_not_contend_for_a_collection_default()
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
            public class Cars_MultiMap : AbstractMultiMapIndexCreationTask<CarCount>
            {
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().BeEmpty(
            "a multi-map names no collection, so it cannot be a second default for Car");
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
