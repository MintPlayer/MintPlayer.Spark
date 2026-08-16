using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Synchronization is a read-modify-write over its own output, so it must reach a fixed point:
/// running it again on an unchanged model must produce identical bytes.
///
/// <para>
/// The first run legitimately differs from its input — it mints ids, synthesizes a breadcrumb and
/// materialises defaults. What must never differ is run 2 against run 3. When that breaks, the
/// symptom is not a crash: it is a repository where every synchronize produces a diff, a
/// regenerate-and-diff gate that fails at random, and — in the case this suite was written for — a
/// model file that grows without bound.
/// </para>
///
/// <para>
/// The fixtures deliberately include a <em>minimal</em> seed with every optional field absent. A
/// fully-populated fixture cannot catch this class of bug, because the mechanism is a field that is
/// missing on write and derived on read.
/// </para>
/// </summary>
public class SynchronizeIdempotencyTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "spark-idem-" + Guid.NewGuid().ToString("N"));

    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();

    public SynchronizeIdempotencyTests()
    {
        Directory.CreateDirectory(_contentRoot);
        _hostEnv.ContentRootPath.Returns(_contentRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelDir => Path.Combine(_contentRoot, "App_Data", "Model");

    private void Synchronize() => new ModelSynchronizer(_hostEnv, _indexRegistry)
        .SynchronizeModels(new IdemContext());

    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(ModelDir, "*.json")
            .ToDictionary(Path.GetFileName, File.ReadAllText, StringComparer.Ordinal)!;

    private void Seed(string fileName, string json)
    {
        Directory.CreateDirectory(ModelDir);
        File.WriteAllText(Path.Combine(ModelDir, fileName), json);
    }

    private int QueryCount(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, fileName)));
        return document.RootElement.GetProperty("queries").GetArrayLength();
    }

    [Fact]
    public void From_an_empty_directory_the_output_is_a_fixed_point()
    {
        Synchronize();
        var second = Snapshot();

        Synchronize();

        Snapshot().Should().BeEquivalentTo(second);
    }

    [Fact]
    public void A_minimal_seed_reaches_a_fixed_point_after_one_run()
    {
        // Every optional field absent. This is the shape that exposes "omitted on write, derived on
        // read" bugs — a fully-populated seed would pass while the bug was live.
        Seed("IdemProbe.json", """
        {
          "persistentObject": {
            "id": "12345678-1234-1234-1234-123456789abc",
            "name": "IdemProbe",
            "clrType": "MintPlayer.Spark.Tests.Model.IdemProbe",
            "attributes": [ { "id": "abcdefab-1234-1234-1234-123456789abc", "name": "Name" } ]
          },
          "queries": [
            { "id": "fedcba98-1234-1234-1234-123456789abc", "name": "GetIdemProbes", "source": "Database.IdemProbes" }
          ]
        }
        """);

        Synchronize();
        var first = Snapshot();

        Synchronize();

        Snapshot().Should().BeEquivalentTo(first,
            "the first run canonicalises the seed; every run after it must be a no-op");
    }

    [Fact]
    public void An_orphaned_model_file_does_not_grow_the_live_entity_file()
    {
        // The regression this suite exists for. An orphan is a model file whose type is no longer a
        // context root nor a reachable embedded type, so it is never rewritten — which is what you
        // get by removing or renaming an entity without deleting its JSON. Its inline query used to
        // be re-read and re-appended on every run, growing the live file without bound: measured at
        // +1 query and +379 bytes per synchronize.
        Synchronize();
        var liveQueriesBefore = QueryCount("IdemProbe.json");

        Seed("IdemGhost.json", """
        {
          "persistentObject": {
            "id": "99999999-9999-9999-9999-999999999999",
            "name": "IdemGhost",
            "clrType": "MintPlayer.Spark.Tests.Model.IdemGhost",
            "attributes": []
          },
          "queries": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "GetGhosts",
              "source": "Database.Ghosts",
              "entityType": "IdemProbe"
            }
          ]
        }
        """);

        Synchronize();
        var afterFirst = QueryCount("IdemProbe.json");

        Synchronize();
        Synchronize();

        QueryCount("IdemProbe.json").Should().Be(afterFirst,
            "an orphaned file must not add a copy of its query on every run");
        afterFirst.Should().Be(liveQueriesBefore + 1,
            "the orphan's query is adopted once, not repeatedly");
        File.Exists(Path.Combine(ModelDir, "IdemGhost.json")).Should().BeTrue(
            "synchronize does not delete files it no longer recognises");
    }

    [Fact]
    public void An_orphaned_file_whose_entity_type_differs_only_by_case_does_not_grow_the_file()
    {
        // The match is OrdinalIgnoreCase, so a differently-cased entityType reaches the same entity.
        Synchronize();

        Seed("IdemGhost.json", """
        {
          "persistentObject": {
            "id": "99999999-9999-9999-9999-999999999999",
            "name": "IdemGhost",
            "clrType": "MintPlayer.Spark.Tests.Model.IdemGhost",
            "attributes": []
          },
          "queries": [
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "GetGhostsCased",
              "source": "Database.Ghosts",
              "entityType": "idemPROBE"
            }
          ]
        }
        """);

        Synchronize();
        var afterFirst = QueryCount("IdemProbe.json");

        Synchronize();
        Synchronize();

        QueryCount("IdemProbe.json").Should().Be(afterFirst);
    }

    [Fact]
    public void A_hand_authored_model_with_preserved_fields_reaches_a_fixed_point()
    {
        // Pins the #253 preservation path against this invariant too: a virtual attribute with no CLR
        // property, plus hand-set presentation fields, must survive unchanged run after run.
        Seed("IdemProbe.json", """
        {
          "persistentObject": {
            "id": "12345678-1234-1234-1234-123456789abc",
            "name": "IdemProbe",
            "clrType": "MintPlayer.Spark.Tests.Model.IdemProbe",
            "breadcrumb": "{Name}",
            "attributes": [
              {
                "id": "abcdefab-1234-1234-1234-123456789abc",
                "name": "Name",
                "dataType": "string",
                "order": 1,
                "label": { "en": "Name", "nl": "Naam" },
                "renderer": "bold",
                "rules": [ { "type": "maxLength", "value": 200 } ]
              },
              {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "name": "Virtual",
                "dataType": "string",
                "order": 99,
                "isReadOnly": true,
                "editMode": "inline"
              }
            ]
          },
          "queries": []
        }
        """);

        Synchronize();
        var first = Snapshot();

        Synchronize();
        Synchronize();

        Snapshot().Should().BeEquivalentTo(first);
        first["IdemProbe.json"].Should().Contain("\"Virtual\"", "the virtual attribute must survive");
        first["IdemProbe.json"].Should().Contain("\"bold\"", "hand-set presentation must survive");
    }

    [Fact]
    public void A_duplicate_attribute_name_is_reported_rather_than_crashing_the_command()
    {
        Seed("IdemProbe.json", """
        {
          "persistentObject": {
            "id": "12345678-1234-1234-1234-123456789abc",
            "name": "IdemProbe",
            "clrType": "MintPlayer.Spark.Tests.Model.IdemProbe",
            "attributes": [
              { "id": "abcdefab-1234-1234-1234-123456789abc", "name": "Name" },
              { "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "name": "Name" }
            ]
          },
          "queries": []
        }
        """);

        var act = Synchronize;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IdemProbe*Name*more than once*",
                "the raw ToDictionary failure named neither the entity nor the file");
    }
}

public sealed class IdemProbe
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class IdemContext : SparkContext
{
    public IRavenQueryable<IdemProbe> IdemProbes => Session.Query<IdemProbe>();
}

/// <summary>
/// Three defects where synchronization produced a model that misdescribed the code: a reference to a
/// projection that no longer existed, a file deleted by a name collision, and a query that could
/// never come into existence.
/// </summary>
public class SynchronizeCorrectnessTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "spark-correct-" + Guid.NewGuid().ToString("N"));

    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();

    public SynchronizeCorrectnessTests()
    {
        Directory.CreateDirectory(_contentRoot);
        _hostEnv.ContentRootPath.Returns(_contentRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelDir => Path.Combine(_contentRoot, "App_Data", "Model");

    private void Synchronize(IIndexRegistry registry, SparkContext context) =>
        new ModelSynchronizer(_hostEnv, registry).SynchronizeModels(context);

    private JsonElement PersistentObject(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, fileName)));
        return document.RootElement.GetProperty("persistentObject").Clone();
    }

    [Fact]
    public void A_projection_that_is_no_longer_registered_is_cleared_from_the_model()
    {
        // Both fields feed the structural hash, so a stale value does not merely linger — the
        // verifier confirms a reference to a type that no longer exists.
        var withProjection = Substitute.For<IIndexRegistry>();
        withProjection.GetRegistrationForCollectionType(typeof(IdemProbe)).Returns(new IndexRegistration
        {
            IndexName = "Probes/Overview",
            IndexType = typeof(IdemProbe),
            CollectionType = typeof(IdemProbe),
            ProjectionType = typeof(IdemProbeProjection),
        });

        Synchronize(withProjection, new IdemContext());
        PersistentObject("IdemProbe.json").TryGetProperty("queryType", out _).Should().BeTrue();

        // The projection is deleted from the codebase: the registry no longer knows it.
        Synchronize(Substitute.For<IIndexRegistry>(), new IdemContext());

        var po = PersistentObject("IdemProbe.json");
        po.TryGetProperty("queryType", out _).Should().BeFalse("a dead projection reference must not survive");
        po.TryGetProperty("indexName", out _).Should().BeFalse();
    }

    [Fact]
    public void An_entity_sharing_a_projections_simple_name_keeps_its_model_file()
    {
        // Model files are keyed by simple type name, so an entity named the same as some other
        // index's projection resolves to the same path. The stale-projection cleanup runs after all
        // writes, so it used to delete a file this very run had produced — and report success.
        var registry = Substitute.For<IIndexRegistry>();
        registry.GetAllRegistrations().Returns([new IndexRegistration
        {
            IndexName = "Other/Overview",
            IndexType = typeof(IdemProbe),
            CollectionType = typeof(IdemProbe),
            // Different namespace, same simple name as the entity being written.
            ProjectionType = typeof(MintPlayer.Spark.Tests.Collides.IdemProbe),
        }]);

        Synchronize(registry, new IdemContext());

        File.Exists(Path.Combine(ModelDir, "IdemProbe.json")).Should()
            .BeTrue("the cleanup must not delete a file written during the same run");
    }

    [Fact]
    public void A_genuinely_stale_projection_file_is_still_removed()
    {
        // The cleanup exists to migrate directories written before projections were merged into
        // their collection type's file. Narrowing it must not disable it.
        Directory.CreateDirectory(ModelDir);
        var stale = Path.Combine(ModelDir, $"{nameof(IdemProbeProjection)}.json");
        File.WriteAllText(stale, "{ \"persistentObject\": { \"name\": \"IdemProbeProjection\", \"clrType\": \"X\" }, \"queries\": [] }");

        var registry = Substitute.For<IIndexRegistry>();
        registry.GetAllRegistrations().Returns([new IndexRegistration
        {
            IndexName = "Probes/Overview",
            IndexType = typeof(IdemProbe),
            CollectionType = typeof(IdemProbe),
            ProjectionType = typeof(IdemProbeProjection),
        }]);

        Synchronize(registry, new IdemContext());

        File.Exists(stale).Should().BeFalse();
    }

    [Fact]
    public void Two_context_properties_of_the_same_entity_type_each_get_a_query()
    {
        // Both properties map to one IdemProbe.json. Writing per property meant the second write
        // dropped the query the first had added, and the file oscillated between the two forever.
        var registry = Substitute.For<IIndexRegistry>();

        Synchronize(registry, new TwoRootsContext());

        var queries = QueryNames();
        queries.Should().BeEquivalentTo(["GetProbes", "GetArchivedProbes"]);

        Synchronize(registry, new TwoRootsContext());
        Synchronize(registry, new TwoRootsContext());

        QueryNames().Should().BeEquivalentTo(queries, "and it must converge, not oscillate");
    }

    private string[] QueryNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, "IdemProbe.json")));
        return [.. document.RootElement.GetProperty("queries").EnumerateArray()
            .Select(q => q.GetProperty("name").GetString()!)];
    }
}

public sealed class IdemProbeProjection
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class TwoRootsContext : SparkContext
{
    public IRavenQueryable<IdemProbe> Probes => Session.Query<IdemProbe>();
    public IRavenQueryable<IdemProbe> ArchivedProbes => Session.Query<IdemProbe>();
}
