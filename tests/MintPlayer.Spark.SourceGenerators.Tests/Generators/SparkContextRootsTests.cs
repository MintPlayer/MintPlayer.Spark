using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Query roots on the app's <c>SparkContext</c>, so a generated index is reachable the same way a hand-written
/// one is. Fleet and HR write these by hand today; DemoApp omits them entirely.
/// </summary>
public class SparkContextRootsTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    /// <summary>
    /// A stand-in for <c>MintPlayer.Spark.SparkContext</c>, matched by full name, so the fixture compiles
    /// without referencing the runtime assembly (which would drag RavenDB in).
    /// </summary>
    private const string SparkContextStub = """
        namespace MintPlayer.Spark;

        public abstract class SparkContext
        {
            public object Session { get; set; } = null!;
        }
        """;

    private const string Entity = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Car
        {
            public string? Model { get; set; }
        }
        """;

    private static GeneratorRunResult Run(string contextSource, string entity = Entity)
        => GeneratorHarness.Run(
            GeneratorName,
            [entity, contextSource, SparkContextStub],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: "TestApp");

    private static string RootsFile(GeneratorRunResult result)
        => result.GeneratedSources.Single(s => s.HintName == "SparkContextIndexRoots.g.cs").Source;

    [Fact]
    public void Emits_an_index_backed_query_root()
    {
        var generated = RootsFile(Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """));

        generated.Should().Contain("public partial class AppContext");
        generated.Should().Contain(
            "public global::Raven.Client.Documents.Linq.IRavenQueryable<global::TestApp.Indexes.VCar> VCars"
            + " => Session.Query<global::TestApp.Indexes.VCar, global::TestApp.Indexes.Cars_Overview>();");
    }

    /// <summary>
    /// A hand-written root is a legitimate override, so the collision is silent by design — and emitting ours
    /// anyway would be a duplicate-member compile error.
    /// </summary>
    [Fact]
    public void A_hand_written_root_of_the_same_name_wins_silently()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public object VCars => null!;
            }
            """);

        result.GeneratedSources.Should().NotContain(s => s.HintName == "SparkContextIndexRoots.g.cs");
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_INDEX_008");
    }

    [Fact]
    public void A_non_partial_context_is_reported_rather_than_silently_skipped()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public class AppContext : SparkContext { }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_008");
        result.GeneratedSources.Should().NotContain(s => s.HintName == "SparkContextIndexRoots.g.cs");
    }

    [Fact]
    public void A_class_that_is_not_a_SparkContext_gets_nothing()
    {
        var result = Run("""
            namespace TestApp;

            public partial class NotAContext : System.Collections.Generic.List<string> { }
            """);

        result.GeneratedSources.Should().NotContain(s => s.HintName == "SparkContextIndexRoots.g.cs");
    }

    /// <summary>
    /// Root names come from the same naming function as index and companion names, not a second derivation —
    /// two traversals disagreeing is the bug class this avoids.
    /// </summary>
    [Fact]
    public void Root_names_are_the_pluralized_index_entity_name()
    {
        var generated = RootsFile(Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """,
            entity: """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Person
            {
                public string? Name { get; set; }
            }
            """));

        // Person -> People_Overview / VPerson -> VPeople, through the same irregular-plural table.
        generated.Should().Contain("VPeople");
        generated.Should().Contain("global::TestApp.Indexes.People_Overview");
    }

    [Fact]
    public void No_roots_when_there_are_no_generated_indexes()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """, SparkContextStub],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
