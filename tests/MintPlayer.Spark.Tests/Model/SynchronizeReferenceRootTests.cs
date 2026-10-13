using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// How the synchronizer resolves a query-less <c>[Reference(typeof(T))]</c> (<c>docs/default_index_PRD.md</c>, D6, D8,
/// D11): to the query minted for T's <b>plain</b> root, the one the generator marks with <c>[SparkPlainRoot]</c>,
/// whatever other roots of T exist; to no query, with a warning, when T has no plain root; and a minted root query
/// never takes a name or alias another query already holds. The test project does not run the Spark generator, so
/// the fixture contexts carry the markers it would emit.
/// </summary>
public sealed class SynchronizeReferenceRootTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-refroot-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment hostEnvironment = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog indexCatalog = Substitute.For<IIndexCatalog>();

    public SynchronizeReferenceRootTests()
    {
        Directory.CreateDirectory(contentRoot);
        hostEnvironment.ContentRootPath.Returns(contentRoot);
        indexCatalog.GetAllEntries().Returns([]);
        indexCatalog.GetDefaultFor(Arg.Any<Type>()).Returns((IndexCatalogEntry?)null);
        indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelFile(string type) => Path.Combine(contentRoot, "App_Data", "Model", type + ".json");

    /// <summary>Runs the synchronizer and returns what it printed.</summary>
    private string Synchronize(Type context, IReadOnlyList<SparkLibrary>? libraries = null)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            new ModelSynchronizer(hostEnvironment, indexCatalog) { Libraries = libraries ?? [] }.SynchronizeModels(context);
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private EntityTypeFile Read(string type)
        => JsonSerializer.Deserialize<EntityTypeFile>(File.ReadAllText(ModelFile(type)), ReadOptions)!;

    private string? QueryOf(string type, string attribute)
        => Read(type).PersistentObject.Attributes.Single(a => a.Name == attribute).Query;

    /// <summary>Every query the server would load: the libraries' layers with the application's files on top.</summary>
    private List<SparkQuery> ComposedQueries(IReadOnlyList<SparkLibrary> libraries)
    {
        var queries = new List<SparkQuery>();
        foreach (var file in SparkModelFiles.Compose(libraries, Path.Combine(contentRoot, "App_Data", "Model")))
        {
            var model = JsonSerializer.Deserialize<EntityTypeFile>(file.Json, ReadOptions)!;
            foreach (var query in model.Queries)
            {
                query.EntityType ??= model.PersistentObject.Name;
                queries.Add(query);
            }
        }
        return queries;
    }

    // ---- Plain-root precedence (D6) -------------------------------------------------------------------------

    [Fact]
    public void A_reference_resolves_to_the_plain_root_among_several_roots()
    {
        Synchronize(typeof(RrSeveralRootsContext));

        QueryOf(nameof(RrSite), nameof(RrSite.OrganizationId)).Should().Be("GetOrganizations",
            "Organizations is the plain root; ActiveOrganizations is filtered and declared after it, which the old last-wins map picked");
        Read(nameof(RrOrganization)).Queries.Select(q => q.Name).Should().BeEquivalentTo(["GetActiveOrganizations", "GetOrganizations"]);
    }

    [Fact]
    public void A_plain_root_marked_on_a_base_context_counts()
    {
        Synchronize(typeof(RrDerivedContext));

        QueryOf(nameof(RrSite), nameof(RrSite.OrganizationId)).Should().Be("GetOrganizations",
            "[SparkPlainRoot] is not inherited, so the synchronizer walks the base types itself");
    }

    [Fact]
    public void Roots_without_a_plain_one_give_no_query_and_a_warning()
    {
        var output = Synchronize(typeof(RrNoPlainRootContext));

        QueryOf(nameof(RrSite), nameof(RrSite.OrganizationId)).Should().BeNull(
            "a filtered root is never a reference's lookup by default");
        output.Should().Contain("RrSite.OrganizationId").And.Contain("ActiveOrganizations")
            .And.Contain("Session.Query<RrOrganization>()");
    }

    [Fact]
    public void An_explicit_query_wins_over_the_plain_root()
    {
        Synchronize(typeof(RrExplicitContext));

        QueryOf(nameof(RrExplicitSite), nameof(RrExplicitSite.OrganizationId)).Should().Be("GetActiveOrganizations");
    }

    [Fact]
    public void A_view_root_is_never_a_reference_lookup()
    {
        var output = Synchronize(typeof(RrViewOnlyContext));

        QueryOf(nameof(RrSite), nameof(RrSite.OrganizationId)).Should().BeNull();
        output.Should().Contain("RrSite.OrganizationId");
    }

    // ---- The unresolved-reference warning (D8) ---------------------------------------------------------------

    [Fact]
    public void A_writable_reference_to_an_unrooted_type_warns_and_names_the_fix()
    {
        var output = Synchronize(typeof(RrUnrootedContext));

        QueryOf(nameof(RrSite), nameof(RrSite.OrganizationId)).Should().BeNull();
        output.Should().Contain("Warning").And.Contain("RrSite.OrganizationId")
            .And.Contain("public IRavenQueryable<RrOrganization> RrOrganizations => Session.Query<RrOrganization>();");
    }

    [Fact]
    public void A_read_only_reference_to_an_unrooted_type_does_not_warn()
    {
        var output = Synchronize(typeof(RrReadOnlyReferenceContext));

        QueryOf(nameof(RrAuditedSite), nameof(RrAuditedSite.OwnerId)).Should().BeNull();
        output.Should().NotContain("RrAuditedSite.OwnerId");
    }

    /// <summary>
    /// D8 skips read-only references whatever roots T has: a read-only reference opens no picker, so it loses nothing
    /// by resolving to no query. The M6 synchronizer still warned here when T had roots but none plain.
    /// </summary>
    [Fact]
    public void A_read_only_reference_to_a_type_without_a_plain_root_does_not_warn()
    {
        var output = Synchronize(typeof(RrReadOnlyNoPlainRootContext));

        QueryOf(nameof(RrAuditedSite), nameof(RrAuditedSite.OwnerId)).Should().BeNull();
        output.Should().NotContain("RrAuditedSite.OwnerId");
    }

    // ---- D11: a minted root query never collides ------------------------------------------------------------

    /// <summary>
    /// SP3's blocker: the library ships <c>Custom.Organizations</c> under the alias <c>organizations</c>, and the root
    /// query minted for the application's <c>Organizations</c> root derived the same alias, so verify exited 3 and
    /// startup threw ("Two queries resolve to the alias 'organizations'").
    /// </summary>
    [Fact]
    public void A_minted_root_query_takes_the_first_free_alias()
    {
        var library = OrganizationsLibrary();

        var output = Synchronize(typeof(RrLibraryContext), [library]);

        var queries = ComposedQueries([library]);
        var act = () => SparkQueryAliasIndex.Index(queries);
        act.Should().NotThrow("the minted alias steps aside");

        var minted = queries.Single(q => q.Name == "GetRrLibOrganizations");
        minted.Source.Should().Be("Database.RrLibOrganizations");
        minted.Alias.Should().Be("rrliborganizations-2");
        output.Should().Contain("rrliborganizations-2");

        var custom = queries.Single(q => q.Name == "RrLibOrganizations");
        custom.Alias.Should().Be("rrliborganizations", "an existing query keeps its alias");
        custom.Id.Should().Be(LibraryQueryId, "and its id, which program units link to");

        QueryOf(nameof(RrLibSite), nameof(RrLibSite.OrganizationId)).Should().Be("GetRrLibOrganizations",
            "a reference still resolves to the minted root query (D11)");
    }

    [Fact]
    public void A_minted_root_query_takes_the_first_free_name()
    {
        var library = OrganizationsLibrary(otherQueryName: "GetRrLibOrganizations");

        var output = Synchronize(typeof(RrLibraryContext), [library]);

        var queries = ComposedQueries([library]);
        var act = () => SparkQueryAliasIndex.Index(queries);
        act.Should().NotThrow();
        queries.Count(q => q.Name == "GetRrLibOrganizations").Should().Be(1, "the library's query keeps the name");

        var minted = queries.Single(q => q.Source == "Database.RrLibOrganizations");
        minted.Name.Should().Be("GetRrLibOrganizations2");
        output.Should().Contain("GetRrLibOrganizations2");
        QueryOf(nameof(RrLibSite), nameof(RrLibSite.OrganizationId)).Should().Be("GetRrLibOrganizations2",
            "the client looks a reference's query up by name, so it must name the minted one");
    }

    [Fact]
    public void A_renamed_minted_query_is_a_fixed_point()
    {
        var library = OrganizationsLibrary(otherQueryName: "GetRrLibOrganizations");
        Synchronize(typeof(RrLibraryContext), [library]);
        var organizations = File.ReadAllText(ModelFile(nameof(RrLibOrganization)));
        var sites = File.ReadAllText(ModelFile(nameof(RrLibSite)));

        Synchronize(typeof(RrLibraryContext), [library]);

        File.ReadAllText(ModelFile(nameof(RrLibOrganization))).Should().Be(organizations, "the second run finds the minted query instead of minting another");
        File.ReadAllText(ModelFile(nameof(RrLibSite))).Should().Be(sites);
    }

    // ---- D11 for satellite queries ---------------------------------------------------------------------------

    /// <summary>
    /// A satellite's query is minted in the same run as the roots'. It used to be minted outside the plan, so a root
    /// whose derived alias equals the satellite query's took it first, and the satellite then minted the same alias
    /// ("Two queries resolve to the alias 'rrsatnotes'"). The satellite's name is its library's contract, so the root
    /// steps aside.
    /// </summary>
    [Fact]
    public void A_minted_root_query_steps_around_a_satellite_query_minted_in_the_same_run()
    {
        RegisterSatellite();

        Synchronize(typeof(RrSatelliteContext));

        var queries = ComposedQueries([]);
        var act = () => SparkQueryAliasIndex.Index(queries);
        act.Should().NotThrow();

        var satellite = queries.Single(q => q.Source == "Custom.RrSatNotes");
        satellite.Name.Should().Be("RrSatNotes", "the library's name is kept");
        (satellite.Alias ?? SparkQueryAliases.Derive(satellite.Name)).Should().Be("rrsatnotes", "the derived alias is the one the library addresses");

        var root = queries.Single(q => q.Source == "Database.RrSatNotes");
        root.Name.Should().Be("GetRrSatNotes");
        root.Alias.Should().Be("rrsatnotes-2");
    }

    [Fact]
    public void A_satellite_query_steps_aside_from_an_existing_query_warns_and_is_a_fixed_point()
    {
        RegisterSatellite();
        var library = OrganizationsLibrary(otherQueryName: "RrSatNotes");

        var output = Synchronize(typeof(RrSatelliteContext), [library]);

        var queries = ComposedQueries([library]);
        var act = () => SparkQueryAliasIndex.Index(queries);
        act.Should().NotThrow();
        queries.Single(q => q.Source == "Custom.RrSatNotes").Name.Should().Be("RrSatNotes2",
            "the existing query keeps its name; only the query being minted steps aside");
        output.Should().Contain("Warning").And.Contain("RrSatNotes2");

        var notes = File.ReadAllText(ModelFile(nameof(RrSatOwnerNote)));
        Synchronize(typeof(RrSatelliteContext), [library]);
        File.ReadAllText(ModelFile(nameof(RrSatOwnerNote))).Should().Be(notes, "the second run finds the stepped-aside query");
    }

    private static void RegisterSatellite()
        => SparkModelSatellites.Register(new(typeof(RrSatOwner), typeof(RrSatOwnerNote),
            new SparkSatelliteQuery("RrSatNotes", "Custom.RrSatNotes")));

    private static readonly Guid LibraryQueryId =Guid.Parse("0d110000-0000-4000-8000-000000000001");

    /// <summary>
    /// A library shipping <see cref="RrLibOrganization"/> with its own <c>Custom.*</c> query aliased
    /// <c>rrliborganizations</c>, plus a second type whose query takes <paramref name="otherQueryName"/>.
    /// </summary>
    private static SparkLibrary OrganizationsLibrary(string otherQueryName = "RrLibMemberships")
    {
        var organization = new JsonObject
        {
            ["persistentObject"] = new JsonObject
            {
                ["id"] = "0d110000-0000-4000-8000-000000000010",
                ["name"] = nameof(RrLibOrganization),
                ["clrType"] = typeof(RrLibOrganization).FullName,
                ["attributes"] = new JsonArray(new JsonObject
                {
                    ["id"] = "0d110000-0000-4000-8000-000000000011",
                    ["name"] = nameof(RrLibOrganization.Name),
                    ["dataType"] = "string",
                    ["order"] = 1,
                }),
            },
            ["queries"] = new JsonArray(new JsonObject
            {
                ["id"] = LibraryQueryId.ToString(),
                ["name"] = "RrLibOrganizations",
                ["alias"] = "rrliborganizations",
                ["source"] = "Custom.RrLibOrganizations",
            }),
        };
        var membership = new JsonObject
        {
            ["persistentObject"] = new JsonObject
            {
                ["id"] = "0d110000-0000-4000-8000-000000000020",
                ["name"] = "RrLibMembership",
                ["clrType"] = "Library.RrLibMembership",
                ["attributes"] = new JsonArray(),
            },
            ["queries"] = new JsonArray(new JsonObject
            {
                ["id"] = "0d110000-0000-4000-8000-000000000021",
                ["name"] = otherQueryName,
                ["alias"] = "rrlibmemberships",
                ["source"] = "Custom.RrLibMemberships",
            }),
        };
        return new SparkLibrary("rrlib", "RrLib", [],
        [
            new SparkLibraryLayer("model", "Model/RrLibOrganization.json", organization.ToJsonString()),
            new SparkLibraryLayer("model", "Model/RrLibMembership.json", membership.ToJsonString()),
        ]);
    }
}

// ---- Fixtures ------------------------------------------------------------------------------------------------

public sealed class RrOrganization
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool IsActive { get; set; }
}

[FromIndex(typeof(RrOrganizationsIndex))]
public sealed class VRrOrganization
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class RrOrganizationsIndex;

public sealed class RrSite
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    [Reference(typeof(RrOrganization))]
    public string? OrganizationId { get; set; }
}

public sealed class RrExplicitSite
{
    public string? Id { get; set; }

    [Reference(typeof(RrOrganization), "GetActiveOrganizations")]
    public string? OrganizationId { get; set; }
}

public sealed class RrAuditedSite
{
    public string? Id { get; set; }

    [Reference(typeof(RrOrganization)), System.ComponentModel.ReadOnly(true)]
    public string? OwnerId { get; set; }
}

[SparkPlainRoot(typeof(RrSite), nameof(Sites))]
[SparkPlainRoot(typeof(RrOrganization), nameof(Organizations))]
public sealed class RrSeveralRootsContext : SparkContext
{
    public IRavenQueryable<RrSite> Sites => Session.Query<RrSite>();
    public IRavenQueryable<RrOrganization> Organizations => Session.Query<RrOrganization>();
    public IRavenQueryable<RrOrganization> ActiveOrganizations => Session.Query<RrOrganization>().Where(o => o.IsActive);
}

[SparkPlainRoot(typeof(RrOrganization), nameof(Organizations))]
public abstract class RrBaseContext : SparkContext
{
    public IRavenQueryable<RrOrganization> Organizations => Session.Query<RrOrganization>();
}

[SparkPlainRoot(typeof(RrSite), nameof(Sites))]
public sealed class RrDerivedContext : RrBaseContext
{
    public IRavenQueryable<RrSite> Sites => Session.Query<RrSite>();
    public IRavenQueryable<RrOrganization> ActiveOrganizations => Session.Query<RrOrganization>().Where(o => o.IsActive);
}

[SparkPlainRoot(typeof(RrSite), nameof(Sites))]
public sealed class RrNoPlainRootContext : SparkContext
{
    public IRavenQueryable<RrSite> Sites => Session.Query<RrSite>();
    public IRavenQueryable<RrOrganization> ActiveOrganizations => Session.Query<RrOrganization>().Where(o => o.IsActive);
}

[SparkPlainRoot(typeof(RrExplicitSite), nameof(Sites))]
[SparkPlainRoot(typeof(RrOrganization), nameof(Organizations))]
public sealed class RrExplicitContext : SparkContext
{
    public IRavenQueryable<RrExplicitSite> Sites => Session.Query<RrExplicitSite>();
    public IRavenQueryable<RrOrganization> Organizations => Session.Query<RrOrganization>();
    public IRavenQueryable<RrOrganization> ActiveOrganizations => Session.Query<RrOrganization>().Where(o => o.IsActive);
}

[SparkPlainRoot(typeof(RrSite), nameof(Sites))]
public sealed class RrViewOnlyContext : SparkContext
{
    public IRavenQueryable<RrSite> Sites => Session.Query<RrSite>();
    public IRavenQueryable<VRrOrganization> VOrganizations => Session.Query<VRrOrganization>();
}

[SparkPlainRoot(typeof(RrSite), nameof(Sites))]
public sealed class RrUnrootedContext : SparkContext
{
    public IRavenQueryable<RrSite> Sites => Session.Query<RrSite>();
}

[SparkPlainRoot(typeof(RrAuditedSite), nameof(Sites))]
public sealed class RrReadOnlyReferenceContext : SparkContext
{
    public IRavenQueryable<RrAuditedSite> Sites => Session.Query<RrAuditedSite>();
}

[SparkPlainRoot(typeof(RrAuditedSite), nameof(Sites))]
public sealed class RrReadOnlyNoPlainRootContext : SparkContext
{
    public IRavenQueryable<RrAuditedSite> Sites => Session.Query<RrAuditedSite>();
    public IRavenQueryable<RrOrganization> ActiveOrganizations => Session.Query<RrOrganization>().Where(o => o.IsActive);
}

public sealed class RrSatOwner
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>A satellite of <see cref="RrSatOwner"/>, as a contributions type is of its target.</summary>
public sealed class RrSatOwnerNote
{
    public string? Id { get; set; }
    public string? Text { get; set; }
}

public sealed class RrSatNote
{
    public string? Id { get; set; }
    public string? Text { get; set; }
}

[SparkPlainRoot(typeof(RrSatOwner), nameof(RrSatOwners))]
[SparkPlainRoot(typeof(RrSatNote), nameof(RrSatNotes))]
public sealed class RrSatelliteContext : SparkContext
{
    public IRavenQueryable<RrSatOwner> RrSatOwners => Session.Query<RrSatOwner>();
    public IRavenQueryable<RrSatNote> RrSatNotes => Session.Query<RrSatNote>();
}

/// <summary>Stands in for <c>Organization</c>, which <c>libs/organizations</c> ships with its own model and query.</summary>
public sealed class RrLibOrganization
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class RrLibSite
{
    public string? Id { get; set; }

    [Reference(typeof(RrLibOrganization))]
    public string? OrganizationId { get; set; }
}

[SparkPlainRoot(typeof(RrLibSite), nameof(RrLibSites))]
[SparkPlainRoot(typeof(RrLibOrganization), nameof(RrLibOrganizations))]
public sealed class RrLibraryContext : SparkContext
{
    public IRavenQueryable<RrLibSite> RrLibSites => Session.Query<RrLibSite>();
    public IRavenQueryable<RrLibOrganization> RrLibOrganizations => Session.Query<RrLibOrganization>();
}
