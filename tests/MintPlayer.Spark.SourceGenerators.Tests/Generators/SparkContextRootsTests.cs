using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using MetadataReference = Microsoft.CodeAnalysis.MetadataReference;
using OutputKind = Microsoft.CodeAnalysis.OutputKind;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using TestingHarness = MintPlayer.SourceGenerators.TestHarness.GeneratorHarness;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// What the generator contributes to the app's <c>SparkContext</c> (<c>docs/default_index_PRD.md</c>, D6, D7): a typed
/// root for every public <c>[FromIndex]</c> view, generated or hand-written, from this project or a referenced one,
/// under a name that never collides; and <c>[SparkPlainRoot]</c> for every plain entity root.
/// </summary>
public class SparkContextRootsTests
{
    private const string GeneratorName = "GenerateIndexGenerator";
    private const string RootsHintName = "SparkContextIndexRoots.g.cs";

    /// <summary>
    /// A stand-in for <c>MintPlayer.Spark.SparkContext</c>, matched by full name, so the fixture compiles
    /// without referencing the runtime assembly. Its <c>Session</c> is a real RavenDB session type, so the generated
    /// roots and the plain-root classifier bind exactly as they do against the real context.
    /// </summary>
    private const string SparkContextStub = """
        namespace MintPlayer.Spark;

        public abstract class SparkContext
        {
            public Raven.Client.Documents.Session.IAsyncDocumentSession Session { get; set; } = null!;
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

    private static readonly Type[] References =
        [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)];

    private static GeneratorRunResult Run(string contextSource, string entity = Entity, IEnumerable<MetadataReference>? additionalReferences = null)
        => GeneratorHarness.Run(
            GeneratorName,
            [entity, contextSource, SparkContextStub],
            referenceTypes: References,
            rootNamespace: "TestApp",
            outputKind: OutputKind.ConsoleApplication,
            additionalReferences: additionalReferences);

    private static string RootsFile(GeneratorRunResult result)
        => result.GeneratedSources.Single(s => s.HintName == RootsHintName).Source;

    private static IEnumerable<Diagnostic> Errors(GeneratorRunResult result)
        => result.FinalCompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001"); // an app fixture has no Main

    // ---- Generated pairs --------------------------------------------------------------------------------------

    [Fact]
    public void Emits_an_index_backed_query_root()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """);

        var generated = RootsFile(result);
        generated.Should().Contain("public partial class AppContext");
        generated.Should().Contain(
            "public global::Raven.Client.Documents.Linq.IRavenQueryable<global::TestApp.Indexes.VCar> VCars"
            + " => Session.Query<global::TestApp.Indexes.VCar, global::TestApp.Indexes.Cars_Overview>();");
        Errors(result).Should().BeEmpty();
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

    /// <summary>
    /// A hand-written root of the same view already exposes it: a second, renamed root would only be noise.
    /// </summary>
    [Fact]
    public void A_hand_written_root_of_the_same_view_wins_silently()
    {
        var result = Run("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Indexes;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<VCar> VCars => Session.Query<VCar, Cars_Overview>();
            }
            """);

        result.GeneratedSources.Should().NotContain(s => s.HintName == RootsHintName);
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_INDEX_008" || d.Id == "SPARK_INDEX_013");
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
        result.GeneratedSources.Should().NotContain(s => s.HintName == RootsHintName);
    }

    /// <summary>The generated half reopens the containing type too, so a non-partial container is the same failure.</summary>
    [Fact]
    public void A_partial_context_inside_a_non_partial_type_is_reported()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public class Outer
            {
                public partial class AppContext : SparkContext { }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_008");
        result.GeneratedSources.Should().NotContain(s => s.HintName == RootsHintName);
    }

    [Fact]
    public void A_class_that_is_not_a_SparkContext_gets_nothing()
    {
        var result = Run("""
            namespace TestApp;

            public partial class NotAContext : System.Collections.Generic.List<string> { }
            """);

        result.GeneratedSources.Should().NotContain(s => s.HintName == RootsHintName);
    }

    [Fact]
    public void No_roots_when_there_are_no_views()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """, SparkContextStub],
            referenceTypes: References,
            rootNamespace: "TestApp",
            outputKind: OutputKind.ConsoleApplication);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    // ---- Hand-written and referenced views (D6) ----------------------------------------------------------------

    private const string HandWrittenView = """
        using MintPlayer.Spark.Abstractions;
        using Raven.Client.Documents.Indexes;

        namespace TestApp.Views;

        public class Person { public string? Name { get; set; } }

        public class People_ByName : AbstractIndexCreationTask<Person> { }

        [FromIndex(typeof(People_ByName))]
        public class VPersonName { public string? Name { get; set; } }
        """;

    [Fact]
    public void A_hand_written_view_and_index_get_a_root()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """, entity: HandWrittenView);

        RootsFile(result).Should().Contain(
            "public global::Raven.Client.Documents.Linq.IRavenQueryable<global::TestApp.Views.VPersonName> VPersonNames"
            + " => Session.Query<global::TestApp.Views.VPersonName, global::TestApp.Views.People_ByName>();");
        Errors(result).Should().BeEmpty();
    }

    /// <summary>The root is a public property, so a non-public view would be CS0053: it gets none.</summary>
    [Fact]
    public void An_internal_view_gets_no_root()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """, entity: HandWrittenView.Replace("public class VPersonName", "internal class VPersonName"));

        result.GeneratedSources.Should().NotContain(s => s.HintName == RootsHintName);
    }

    [Fact]
    public void A_view_from_a_referenced_assembly_gets_a_root()
    {
        var library = GeneratorHarness.CompileToMetadataReference("TestLibrary", [HandWrittenView], References);

        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """, entity: "// the view comes from the library", additionalReferences: [library]);

        RootsFile(result).Should().Contain(
            "IRavenQueryable<global::TestApp.Views.VPersonName> VPersonNames"
            + " => Session.Query<global::TestApp.Views.VPersonName, global::TestApp.Views.People_ByName>();");
        Errors(result).Should().BeEmpty();
    }

    // ---- Naming (D7) --------------------------------------------------------------------------------------------

    [Fact]
    public void A_name_taken_by_an_unrelated_member_moves_to_the_first_free_one_and_says_so()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public object VCars => null!;
                public object VCars2 => null!;
            }
            """);

        RootsFile(result).Should().Contain("IRavenQueryable<global::TestApp.Indexes.VCar> VCars3 =>");
        var renamed = result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK_INDEX_013").Which;
        renamed.Severity.Should().Be(DiagnosticSeverity.Info);
        renamed.GetMessage().Should().Contain("'VCars3'");
        Errors(result).Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ <c>GetMembers()</c> misses inherited members (PRD §7, SP2): the old generator emitted a root that hid a base
    /// context's member. A private base member neither collides nor hides, so it does not count.
    /// </summary>
    [Fact]
    public void A_name_taken_by_an_inherited_member_moves_and_a_private_one_does_not()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public abstract class AppBase : SparkContext
            {
                public string VCars => "taken";
                private string VPeople => "private, so free";
            }

            public partial class AppContext : AppBase { }
            """,
            entity: Entity + """

            [GenerateIndex]
            public class Person { public string? Name { get; set; } }
            """);

        var generated = RootsFile(result);
        generated.Should().Contain("IRavenQueryable<global::TestApp.Indexes.VCar> VCars2 =>");
        generated.Should().Contain("IRavenQueryable<global::TestApp.Indexes.VPerson> VPeople =>");
        result.GeneratorDiagnostics.Where(d => d.Id == "SPARK_INDEX_013").Should().ContainSingle();
    }

    /// <summary>Roots generated in the same pass count as taken: two views wanting one name get two names.</summary>
    [Fact]
    public void Two_views_with_the_same_name_get_distinct_roots()
    {
        var result = Run("""
            using MintPlayer.Spark;

            namespace TestApp;

            public partial class AppContext : SparkContext { }
            """, entity: """
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;

            namespace TestApp.Fleet
            {
                public class Car { public string? Model { get; set; } }
                public class Cars_ByModel : AbstractIndexCreationTask<Car> { }
                [FromIndex(typeof(Cars_ByModel))] public class VCar { public string? Model { get; set; } }
            }

            namespace TestApp.Rental
            {
                public class Car { public string? Model { get; set; } }
                public class Cars_ByModel : AbstractIndexCreationTask<Car> { }
                [FromIndex(typeof(Cars_ByModel))] public class VCar { public string? Model { get; set; } }
            }
            """);

        var generated = RootsFile(result);
        generated.Should().Contain("IRavenQueryable<global::TestApp.Fleet.VCar> VCars =>");
        generated.Should().Contain("IRavenQueryable<global::TestApp.Rental.VCar> VCars2 =>");
        Errors(result).Should().BeEmpty();
    }

    // ---- The plain-root marker (D6, SP3) -----------------------------------------------------------------------

    private const string Bike = """
        namespace TestApp.Entities;

        public class Bike { public string? Model { get; set; } public bool Archived { get; set; } }
        """;

    [Fact]
    public void Plain_roots_are_marked_in_every_body_form()
    {
        var result = Run("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Bike> Bikes => Session.Query<Bike>();
                public IRavenQueryable<Bike> GetterArrowBikes { get => this.Session.Query<Bike>(); }
                public IRavenQueryable<Bike> BlockBikes { get { return Session.Query<Bike>(); } }
            }
            """, entity: Bike);

        var generated = RootsFile(result);
        foreach (var root in new[] { "Bikes", "GetterArrowBikes", "BlockBikes" })
        {
            generated.Should().Contain(
                $"[global::MintPlayer.Spark.Abstractions.SparkPlainRootAttribute(typeof(global::TestApp.Entities.Bike), nameof(global::TestApp.AppContext.{root}))]");
        }
        Errors(result).Should().BeEmpty();
    }

    [Fact]
    public void Roots_that_are_not_plain_are_not_marked()
    {
        var result = Run("""
            using System.Linq;
            using MintPlayer.Spark;
            using Raven.Client.Documents;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Bike> Bikes => Session.Query<Bike>();
                public IRavenQueryable<Bike> ArchivedBikes => Session.Query<Bike>().Where(b => b.Archived);
                public IRavenQueryable<Bike> IndexBikes => Session.Query<Bike>("Bikes/ByModel");
                public IRavenQueryable<Bike> CollectionBikes => Session.Query<Bike>(collectionName: "OtherBikes");
                public IRavenQueryable<Bike> CustomizedBikes => Session.Query<Bike>().Customize(x => x.WaitForNonStaleResults());
                public IRavenQueryable<Bike> HelperBikes => Q<Bike>();
                public IRavenQueryable<Bike> LocalBikes { get { var q = Session.Query<Bike>(); return q; } }
                private IRavenQueryable<T> Q<T>() => Session.Query<T>();
            }
            """, entity: Bike);

        var generated = RootsFile(result);
        generated.Should().Contain("nameof(global::TestApp.AppContext.Bikes))]");
        generated.Split("SparkPlainRootAttribute(").Length.Should().Be(2, "only Bikes is plain");
        Errors(result).Should().BeEmpty();
    }

    /// <summary>
    /// A root inherited from a base context in the same compilation is marked on the derived context; the
    /// synchronizer reads the markers walking base types, and <c>nameof(Derived.BaseRoot)</c> binds.
    /// </summary>
    [Fact]
    public void An_inherited_same_compilation_root_is_marked_on_the_derived_context()
    {
        var result = Run("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public abstract class AppBase : SparkContext
            {
                public IRavenQueryable<Bike> Bikes => Session.Query<Bike>();
            }

            public partial class AppContext : AppBase { }
            """, entity: Bike);

        RootsFile(result).Should().Contain(
            "[global::MintPlayer.Spark.Abstractions.SparkPlainRootAttribute(typeof(global::TestApp.Entities.Bike), nameof(global::TestApp.AppContext.Bikes))]");
        Errors(result).Should().BeEmpty();
    }

    /// <summary>The partial is needed for the marker alone, so a non-partial context with only plain roots is reported.</summary>
    [Fact]
    public void A_non_partial_context_with_only_plain_roots_is_reported()
    {
        var result = Run("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Bike> Bikes => Session.Query<Bike>();
            }
            """, entity: Bike);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_008");
    }

    // ---- Incrementality ---------------------------------------------------------------------------------------

    /// <summary>
    /// An edit the context step does not care about (a method body elsewhere) leaves the resolved plans, and so the
    /// output, cached. Every model on the way compares by value; one compared by reference would regenerate on every
    /// keystroke, which no single-run test can see.
    /// </summary>
    [Fact]
    public void An_unrelated_edit_keeps_the_context_plans_and_the_output_cached()
    {
        var harness = TestingHarness
            .ForAssembly("MintPlayer.Spark.SourceGenerators")
            .AddReferences(References)
            .WithRootNamespace("TestApp");

        var result = harness.RunKeystroke(
            GeneratorName,
            [HandWrittenView, """
                using MintPlayer.Spark;
                using Raven.Client.Documents.Linq;
                using TestApp.Views;

                namespace TestApp;

                public partial class AppContext : SparkContext
                {
                    public IRavenQueryable<Person> People => Session.Query<Person>();
                }

                public static class Unrelated
                {
                    public static int Answer() => 41;
                }
                """, SparkContextStub],
            editIndex: 1,
            edit: text => text.Replace("=> 41;", "=> 42;"));

        result.OutputUnchanged.Should().BeTrue();
        result.WasFullyCached("GenerateIndex.ContextPlans").Should().BeTrue(
            string.Join(", ", result.ReasonsFor("GenerateIndex.ContextPlans")));
        result.OutputsFullyCached.Should().BeTrue(string.Join(", ", result.OutputReasons));
    }
}
