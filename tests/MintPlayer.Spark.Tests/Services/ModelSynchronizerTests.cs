using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins ModelSynchronizer's contract for the <c>--spark-synchronize-model</c> developer
/// command. The class uses reflection to discover <see cref="IRavenQueryable{T}"/> properties
/// on the SparkContext and writes <c>App_Data/Model/{EntityName}.json</c> files. A regression
/// breaks the dev workflow where edits to the C# entity classes auto-propagate to model files.
/// </summary>
public sealed class ModelSynchronizerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();
    private readonly string _modelPath;

    public ModelSynchronizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-modelsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _modelPath = Path.Combine(_tempDir, "App_Data", "Model");
        _hostEnv.ContentRootPath.Returns(_tempDir);
        _indexRegistry.GetAllRegistrations().Returns([]);
        _indexRegistry.IsProjectionType(Arg.Any<Type>()).Returns(false);
        _indexRegistry.GetRegistrationForCollectionType(Arg.Any<Type>()).Returns((IndexRegistration?)null);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ModelSynchronizer CreateSynchronizer() => new(_hostEnv, _indexRegistry);

    private string ModelFile(string entityName) => Path.Combine(_modelPath, $"{entityName}.json");

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Creates_Model_directory_when_missing_even_if_context_has_no_queries()
    {
        var ctx = new EmptyContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Directory.Exists(_modelPath).Should().BeTrue();
        Directory.GetFiles(_modelPath).Should().BeEmpty();
    }

    [Fact]
    public void Writes_one_JSON_file_per_IRavenQueryable_property()
    {
        var ctx = new TwoEntityContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_TestPerson")).Should().BeTrue();
        File.Exists(ModelFile("MS_TestCar")).Should().BeTrue();
    }

    [Fact]
    public void Writes_PersistentObject_with_ClrType_Name_and_default_query()
    {
        var ctx = new SinglePersonContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));
        file.PersistentObject.Should().NotBeNull();
        file.PersistentObject.Name.Should().Be("MS_TestPerson");
        file.PersistentObject.ClrType.Should().Be(typeof(MS_TestPerson).FullName);

        // Default query: Get{PropertyName} sourcing Database.{PropertyName}.
        file.Queries.Should().ContainSingle();
        var query = file.Queries[0];
        query.Name.Should().Be("GetPeople");
        query.Source.Should().Be("Database.People");
        query.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Generates_attribute_definitions_from_entity_properties()
    {
        var ctx = new SinglePersonContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));
        // FirstName, LastName, Age — Id is excluded.
        file.PersistentObject.Attributes.Select(a => a.Name).Should().BeEquivalentTo(["FirstName", "LastName", "Age"]);
    }

    [Fact]
    public void Preserves_existing_PersistentObject_id_and_attribute_ids_on_re_synchronize()
    {
        var ctx = new SinglePersonContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);
        var first = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));
        var firstId = first.PersistentObject.Id;
        var firstAttrIds = first.PersistentObject.Attributes.ToDictionary(a => a.Name, a => a.Id);

        // Run again — synchronize must not mint new IDs for things that already exist.
        sync.SynchronizeModels(ctx);
        var second = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));

        second.PersistentObject.Id.Should().Be(firstId);
        foreach (var attr in second.PersistentObject.Attributes)
        {
            firstAttrIds.Should().ContainKey(attr.Name);
            attr.Id.Should().Be(firstAttrIds[attr.Name]);
        }
    }

    [Fact]
    public void Reference_collection_property_is_typed_Reference_array()
    {
        var ctx = new TaggedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestTagged"));
        var tagIds = file.PersistentObject.Attributes.Single(a => a.Name == "TagIds");
        tagIds.DataType.Should().Be("Reference", "[Reference] List<string> is a multi-reference, not AsDetail");
        tagIds.IsArray.Should().BeTrue("a collection reference round-trips as an array of ids");
        tagIds.ReferenceType.Should().Be(typeof(MS_TestTag).FullName);
    }

    [Fact]
    public void Bare_list_of_primitive_is_scalar_array_not_AsDetail()
    {
        var ctx = new TaggedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestTagged"));
        var labels = file.PersistentObject.Attributes.Single(a => a.Name == "Labels");
        labels.DataType.Should().Be("string", "List<string> takes its element's scalar type, not AsDetail");
        labels.IsArray.Should().BeTrue();
        labels.AsDetailType.Should().BeNull("a list of primitives scaffolds no nested PO type");
    }

    [Fact]
    public void Synthesizes_a_default_breadcrumb_from_the_first_attribute_when_none_authored()
    {
        var ctx = new SinglePersonContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));
        // No Name/FullName/Title, no [Breadcrumb] → first attribute (FirstName).
        file.PersistentObject.Breadcrumb.Should().Be("{FirstName}");
    }

    [Fact]
    public void Breadcrumb_attribute_on_the_entity_is_authoritative()
    {
        var ctx = new BreadcrumbContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_BreadcrumbPerson"));
        file.PersistentObject.Breadcrumb.Should().Be("{LastName}, {FirstName}");
    }

    [Fact]
    public void Breadcrumb_attribute_wins_over_a_preserved_json_value_on_re_synchronize()
    {
        var ctx = new BreadcrumbContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);
        // Tamper with the persisted breadcrumb, then re-sync: the [Breadcrumb] attribute must win.
        var path = ModelFile("MS_BreadcrumbPerson");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{LastName}, {FirstName}", "{FirstName}"));

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(path).PersistentObject.Breadcrumb.Should().Be("{LastName}, {FirstName}");
    }

    [Fact]
    public void Throws_on_breadcrumb_referencing_an_unknown_attribute()
    {
        var ctx = new BadBreadcrumbContext();
        var sync = CreateSynchronizer();

        var act = () => sync.SynchronizeModels(ctx);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown attribute*Nope*");
    }

    [Fact]
    public void Throws_on_breadcrumb_with_unbalanced_braces()
    {
        var ctx = new UnbalancedBreadcrumbContext();
        var sync = CreateSynchronizer();

        var act = () => sync.SynchronizeModels(ctx);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid breadcrumb template*");
    }

    [Fact]
    public void Removes_stale_projection_model_files_listed_in_IndexRegistry()
    {
        // Pre-create a stale Vehicle.json model file. Then register a projection that maps
        // collection MS_TestCar → projection MS_TestVehicle. Synchronize must delete Vehicle.json.
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_TestVehicle"), """{"persistentObject":{"id":"00000000-0000-0000-0000-000000000000","name":"MS_TestVehicle","clrType":"X"}}""");

        var registration = new IndexRegistration
        {
            IndexName = "Cars_Index",
            IndexType = typeof(MS_TestCar),
            CollectionType = typeof(MS_TestCar),
            ProjectionType = typeof(MS_TestVehicle),
        };
        _indexRegistry.GetAllRegistrations().Returns([registration]);

        var ctx = new EmptyContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_TestVehicle")).Should().BeFalse();
    }

    [Fact]
    public void Skips_projection_types_used_directly_as_queryable_property()
    {
        // If a SparkContext exposes IRavenQueryable<TProjection>, the synchronizer should
        // skip it — projection types are merged into their collection's file by the
        // collection-type pass (or simply not written when no collection type is exposed).
        _indexRegistry.IsProjectionType(typeof(MS_TestVehicle)).Returns(true);

        var ctx = new ProjectionOnlyContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_TestVehicle")).Should().BeFalse();
    }
}

// --- Test fixtures (top-level so reflection finds them) ---

public class MS_TestPerson
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class MS_TestCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
}

public class MS_TestVehicle
{
    public string? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class MS_TestTag
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class MS_TestTagged
{
    public string? Id { get; set; }

    [Reference(typeof(MS_TestTag))]
    public List<string> TagIds { get; set; } = [];

    public List<string> Labels { get; set; } = [];
}

[Breadcrumb("{LastName}, {FirstName}")]
public class MS_BreadcrumbPerson
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

[Breadcrumb("{Nope}")]
public class MS_BadBreadcrumb
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
}

[Breadcrumb("{FirstName")]
public class MS_UnbalancedBreadcrumb
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
}

public class EmptyContext : SparkContext { }

public class SinglePersonContext : SparkContext
{
    public IRavenQueryable<MS_TestPerson> People => Session.Query<MS_TestPerson>();
}

public class TwoEntityContext : SparkContext
{
    public IRavenQueryable<MS_TestPerson> People => Session.Query<MS_TestPerson>();
    public IRavenQueryable<MS_TestCar> Cars => Session.Query<MS_TestCar>();
}

public class ProjectionOnlyContext : SparkContext
{
    public IRavenQueryable<MS_TestVehicle> Vehicles => Session.Query<MS_TestVehicle>();
}

public class TaggedContext : SparkContext
{
    public IRavenQueryable<MS_TestTagged> Tagged => Session.Query<MS_TestTagged>();
    public IRavenQueryable<MS_TestTag> Tags => Session.Query<MS_TestTag>();
}

public class BreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_BreadcrumbPerson> People => Session.Query<MS_BreadcrumbPerson>();
}

public class BadBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_BadBreadcrumb> Items => Session.Query<MS_BadBreadcrumb>();
}

public class UnbalancedBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_UnbalancedBreadcrumb> Items => Session.Query<MS_UnbalancedBreadcrumb>();
}
