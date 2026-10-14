using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK009: one <c>[DefaultIndex]</c> claim per entity type per compilation, and SPARK051–055: the shapes a
/// marker may take (<c>docs/default_index_PRD.md</c>, D1, D4, D5). The catalog's freeze-time validation stays
/// authoritative across assemblies; this mirrors it at compile time.
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
    /// A multi-map's type argument is not a collection, so a bare marker on it claims nothing and cannot contend for
    /// Car's default. (It is SPARK052 instead: a multi-map default must list its types, see below.)
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
    /// D5: a hand-written claim beats <c>[GenerateIndex]</c>. The generated index yields and is emitted without the
    /// marker, so the entity is not a second claim and nothing clashes (it used to be SPARK009 on both).
    /// </summary>
    [Fact]
    public async Task A_GenerateIndex_entity_yields_to_a_hand_marked_index()
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
    public async Task The_clash_message_names_the_entity_type()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }

            [DefaultIndex(typeof(Car))]
            public class Cars_Overview : AbstractIndexCreationTask<Car> { }

            [DefaultIndex]
            public class Cars_Search : AbstractIndexCreationTask<Car> { }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2)
            .And.Contain(d => d.GetMessage(null).Contains("entity type 'TestApp.Car'"));
    }

    // ---- Zero claims among several viewed indexes (D3) ------------------------------------------------------

    private const string TwoViewedCarIndexes = """
        using MintPlayer.Spark.Abstractions;
        using Raven.Client.Documents.Indexes;

        namespace TestApp;

        public class Cars_Overview : AbstractIndexCreationTask<Car> { }
        public class Cars_Search : AbstractIndexCreationTask<Car> { }

        [FromIndex(typeof(Cars_Overview))]
        public class VCar { public string? Model { get; set; } }

        [FromIndex(typeof(Cars_Search))]
        public class VCarSearch { public string? Model { get; set; } }
        """;

    /// <summary>
    /// Without a claim only the sole viewed index of T is its default, so two viewed indexes and no claim is the
    /// catalog's startup error. SPARK009 says so at compile time, on both indexes.
    /// </summary>
    [Fact]
    public async Task Two_viewed_indexes_and_no_claim_are_flagged_on_both()
    {
        var diagnostics = await RunAsync(TwoViewedCarIndexes + """

            public class Car { public string? Model { get; set; } }
            """);

        var spark009 = diagnostics.Where(d => d.Id == "SPARK009").ToList();
        spark009.Should().HaveCount(2);
        spark009.Should().Contain(d => d.GetMessage(null).Contains("none claims it") && d.GetMessage(null).Contains("Cars_Overview, Cars_Search"));
    }

    [Fact]
    public async Task Two_viewed_indexes_with_one_claim_are_clean()
    {
        var diagnostics = await RunAsync(TwoViewedCarIndexes.Replace(
            "public class Cars_Overview", "[DefaultIndex] public class Cars_Overview") + """

            public class Car { public string? Model { get; set; } }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>A [GenerateIndex] entity's generated index is a viewed index that claims T (it yields only to a hand-written claim).</summary>
    [Fact]
    public async Task A_GenerateIndex_entity_counts_as_a_claim_beside_two_viewed_indexes()
    {
        var diagnostics = await RunAsync(TwoViewedCarIndexes + """

            [GenerateIndex]
            public class Car { public string? Model { get; set; } }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task One_viewed_index_and_an_unviewed_one_are_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }
            public class Cars_Overview : AbstractIndexCreationTask<Car> { }
            public class Cars_Search : AbstractIndexCreationTask<Car> { }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar { public string? Model { get; set; } }
            """);

        diagnostics.Should().BeEmpty("the sole viewed index is the implicit default");
    }

    /// <summary>Two views of one index are one viewed index.</summary>
    [Fact]
    public async Task Two_views_of_one_index_are_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Car { public string? Model { get; set; } }
            public class Cars_Overview : AbstractIndexCreationTask<Car> { }

            [FromIndex(typeof(Cars_Overview))]
            public class VCar { public string? Model { get; set; } }

            [FromIndex(typeof(Cars_Overview))]
            public class VCarSummary { public string? Model { get; set; } }
            """);

        diagnostics.Should().BeEmpty();
    }

    // ---- Multi-map claims (D1, D4) -------------------------------------------------------------------------

    private const string Vehicles = """
        namespace TestApp;

        public class Car { public string? Name { get; set; } }
        public class Bike { public string? Name { get; set; } }
        public class Truck { public string? Name { get; set; } }
        public class VehicleEntry { public string? Name { get; set; } }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunWithVehiclesAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [Vehicles, source],
            referenceTypes: [typeof(DefaultIndexAttribute), typeof(AbstractIndexCreationTask<>), typeof(MintPlayer.Spark.SparkMultiMapIndexCreationTask<>)]);

    [Fact]
    public async Task A_Spark_multi_map_listing_its_mapped_types_is_clean()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            [DefaultIndex(typeof(Car), typeof(Bike))]
            public class Vehicles : SparkMultiMapIndexCreationTask<VehicleEntry>
            {
                public Vehicles()
                {
                    AddMap<Car>(cars => from c in cars select new VehicleEntry { Name = c.Name });
                    // The inferred form binds to the same generic AddMap<Bike>.
                    AddMap((IEnumerable<Bike> bikes) => from b in bikes select new VehicleEntry { Name = b.Name });
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_multi_map_claim_clashes_with_a_single_map_claim_for_the_same_type()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;
            using System.Linq;

            namespace TestApp;

            [DefaultIndex(typeof(Car), typeof(Bike))]
            public class Vehicles : SparkMultiMapIndexCreationTask<VehicleEntry>
            {
                public Vehicles()
                {
                    AddMap<Car>(cars => from c in cars select new VehicleEntry { Name = c.Name });
                    AddMap<Bike>(bikes => from b in bikes select new VehicleEntry { Name = b.Name });
                }
            }

            [DefaultIndex]
            public class Bikes_Overview : AbstractIndexCreationTask<Bike> { }
            """);

        diagnostics.Where(d => d.Id == "SPARK009").Should().HaveCount(2)
            .And.OnlyContain(d => d.GetMessage(null).Contains("'TestApp.Bike'"));
    }

    [Fact]
    public async Task A_bare_marker_on_a_multi_map_is_an_error()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using System.Linq;

            namespace TestApp;

            [DefaultIndex]
            public class Vehicles : SparkMultiMapIndexCreationTask<VehicleEntry>
            {
                public Vehicles()
                {
                    AddMap<Car>(cars => from c in cars select new VehicleEntry { Name = c.Name });
                }
            }
            """);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK052");
    }

    [Fact]
    public async Task A_listed_type_the_multi_map_does_not_map_is_an_error()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using System.Linq;

            namespace TestApp;

            [DefaultIndex(typeof(Car), typeof(Truck))]
            public class Vehicles : SparkMultiMapIndexCreationTask<VehicleEntry>
            {
                public Vehicles()
                {
                    AddMap<Car>(cars => from c in cars select new VehicleEntry { Name = c.Name });
                }
            }
            """);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK053");
        diagnostic.GetMessage(null).Should().Contain("'TestApp.Truck'");
    }

    [Fact]
    public async Task A_multi_map_default_not_deriving_from_the_Spark_base_is_an_error()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;
            using System.Linq;

            namespace TestApp;

            [DefaultIndex(typeof(Car))]
            public class Vehicles : AbstractMultiMapIndexCreationTask<VehicleEntry>
            {
                public Vehicles()
                {
                    AddMap<Car>(cars => from c in cars select new VehicleEntry { Name = c.Name });
                }
            }
            """);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK055");
    }

    // ---- Single-map arguments and JavaScript indexes (D1, SP1) ---------------------------------------------

    [Fact]
    public async Task A_single_map_naming_its_own_type_is_clean()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            [DefaultIndex(typeof(Car))]
            public class Cars_Overview : AbstractIndexCreationTask<Car> { }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_map_naming_another_type_is_an_error()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            [DefaultIndex(typeof(Bike))]
            public class Cars_Overview : AbstractIndexCreationTask<Car> { }
            """);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK051");
        diagnostic.GetMessage(null).Should().Contain("'TestApp.Bike'");
    }

    [Fact]
    public async Task A_marker_on_a_JavaScript_index_is_an_error()
    {
        var diagnostics = await RunWithVehiclesAsync("""
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            [DefaultIndex(typeof(Car))]
            public class Cars_ByJs : AbstractJavaScriptIndexCreationTask { }
            """);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK054");
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
