using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK056 (<c>docs/default_index_PRD.md</c>, D8): a writable <c>[Reference(typeof(T))]</c> that names no query, where
/// the context has no entity root of <c>T</c>, gets no query from the synchronizer, and so an empty picker — the
/// SeoTester trigger (PRD §1). Run over the compilation <b>after</b> generation, as <c>csc</c> runs it, so the
/// generator's view roots and <c>[SparkPlainRoot]</c> markers are in it.
/// </summary>
public class MissingReferenceRootAnalyzerTests
{
    private static readonly Type[] References =
    [
        typeof(ReferenceAttribute),
        typeof(SparkContext),
        typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
        typeof(System.ComponentModel.ReadOnlyAttribute),
    ];

    private const string Entities = """
        using System.ComponentModel;
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        public class Organization { public string? Id { get; set; } public string? Name { get; set; } }

        public class Site
        {
            public string? Id { get; set; }

            [Reference(typeof(Organization))]
            public string? OrganizationId { get; set; }
        }
        """;

    private static async Task<IReadOnlyList<Diagnostic>> RunAsync(string context, string entities = Entities, IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var result = await GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            "GenerateIndexGenerator",
            [GeneratorHarness.CreateAnalyzer("MissingReferenceRootAnalyzer")],
            [("Entities.cs", entities), ("AppContext.cs", context)],
            referenceTypes: References,
            rootNamespace: "TestApp",
            additionalReferences: additionalReferences);

        // A fixture that does not compile analyzes to nothing, and "clean" would pass for the wrong reason.
        result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        return result.Diagnostics.Where(d => d.Id == "SPARK056").ToList();
    }

    [Fact]
    public async Task A_hand_written_entity_root_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
                public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// A view root mints no query (the synchronizer skips projection roots), so it does not silence the warning —
    /// even though the generator emitted it onto the very context the analyzer reads.
    /// </summary>
    [Fact]
    public async Task Only_a_view_root_warns()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
            }
            """,
            entities: Entities + """

            public class Organizations_Overview : Raven.Client.Documents.Indexes.AbstractIndexCreationTask<Organization> { }

            [FromIndex(typeof(Organizations_Overview))]
            public class VOrganization { public string? Name { get; set; } }
            """);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("'Site.OrganizationId' references 'TestApp.Entities.Organization'");
        diagnostic.GetMessage().Should().Contain("public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();");
    }

    /// <summary>The trigger's shape: the target type comes from a library, and nothing roots it.</summary>
    [Fact]
    public async Task A_library_type_with_no_root_warns()
    {
        var library = GeneratorHarness.CompileToMetadataReference(
            "OrganizationsLibrary",
            ["""
                namespace Organizations;

                public class Organization { public string? Id { get; set; } public string? Name { get; set; } }
                """]);

        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
            }
            """,
            entities: """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Site
            {
                public string? Id { get; set; }

                [Reference(typeof(Organizations.Organization))]
                public string? OrganizationId { get; set; }
            }
            """,
            additionalReferences: [library]);

        diagnostics.Should().ContainSingle().Which.Properties["EntityType"].Should().Be("Organizations.Organization");
    }

    [Fact]
    public async Task A_read_only_reference_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
            }
            """,
            entities: Entities.Replace(
                "[Reference(typeof(Organization))]",
                "[Reference(typeof(Organization)), ReadOnly(true)]"));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reference_naming_its_query_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
            }
            """,
            entities: Entities.Replace(
                "[Reference(typeof(Organization))]",
                "[Reference(typeof(Organization), \"MyOrganizations\")]"));

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// SP3 found the plain root can be identified, so several roots of <c>T</c> are fine: the reference resolves to the
    /// plain one, which the generator marks.
    /// </summary>
    [Fact]
    public async Task Several_roots_including_the_plain_one_are_clean()
    {
        var diagnostics = await RunAsync("""
            using System.Linq;
            using MintPlayer.Spark;
            using Raven.Client.Documents;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public partial class AppContext : SparkContext
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
                public IRavenQueryable<Organization> NamedOrganizations => Session.Query<Organization>().Where(o => o.Name != null);
                public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>A root inherited from a base context is a root.</summary>
    [Fact]
    public async Task An_inherited_root_is_clean()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;
            using TestApp.Entities;

            namespace TestApp;

            public abstract class AppBase : SparkContext
            {
                public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();
            }

            public partial class AppContext : AppBase
            {
                public IRavenQueryable<Site> Sites => Session.Query<Site>();
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>A library has no context; its references are judged in the application that roots them.</summary>
    [Fact]
    public async Task A_compilation_without_a_context_is_clean()
    {
        var diagnostics = await RunAsync("// no context in this project");

        diagnostics.Should().BeEmpty();
    }

    // ---- The code fix -----------------------------------------------------------------------------------------

    /// <summary>
    /// The fix adds the root to the context, in the context's document, the way the app contexts write theirs, with
    /// the usings it needs: the target type is a library's, in a namespace the context file does not import.
    /// </summary>
    [Fact]
    public async Task The_fix_adds_the_entity_root_to_the_context()
    {
        var result = await CodeFixHarness.RunAnalyzerFixAsync(
            "MissingReferenceRootAnalyzer",
            "MissingReferenceRootCodeFixProvider",
            "SPARK056",
            [
                FixtureProject.Of("OrganizationsLibrary", ("Organization.cs", """
                    namespace Organizations;

                    public class Organization { public string? Id { get; set; } public string? Name { get; set; } }
                    """)),
                FixtureProject.Of("App",
                    ("Site.cs", """
                        using MintPlayer.Spark.Abstractions;

                        namespace TestApp.Entities;

                        public class Site
                        {
                            public string? Id { get; set; }

                            [Reference(typeof(Organizations.Organization))]
                            public string? OrganizationId { get; set; }
                        }
                        """),
                    ("AppContext.cs", """
                        using MintPlayer.Spark;
                        using Raven.Client.Documents.Linq;
                        using TestApp.Entities;

                        namespace TestApp;

                        public partial class AppContext : SparkContext
                        {
                            public IRavenQueryable<Site> Sites => Session.Query<Site>();

                            public int Unrelated { get; set; }
                        }
                        """)),
            ],
            referenceTypes: References);

        result.Diagnostics.Should().ContainSingle();
        result.OfferedTitles.Should().Equal("Add the root 'Organizations' to 'AppContext'");

        var context = result.Document("AppContext.cs");
        context.Should().Contain("using Organizations;");
        context.Should().Contain(
            "    public IRavenQueryable<Site> Sites => Session.Query<Site>();\n"
            + "    public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();\n");
        result.Document("Site.cs").Should().NotContain("IRavenQueryable");
    }
}
