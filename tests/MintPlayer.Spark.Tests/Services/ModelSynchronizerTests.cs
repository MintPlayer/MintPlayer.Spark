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
    public void Sortable_attribute_on_AsDetail_array_sets_IsSortable_true()
    {
        var ctx = new OrderedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        var steps = file.PersistentObject.Attributes.Single(a => a.Name == "Steps");
        steps.DataType.Should().Be("AsDetail");
        steps.IsArray.Should().BeTrue();
        steps.IsSortable.Should().BeTrue("[Sortable] on an AsDetail array opts it into drag-reorder");
    }

    [Fact]
    public void AsDetail_array_without_Sortable_leaves_IsSortable_null()
    {
        var ctx = new OrderedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        var notes = file.PersistentObject.Attributes.Single(a => a.Name == "Notes");
        notes.DataType.Should().Be("AsDetail");
        notes.IsArray.Should().BeTrue();
        notes.IsSortable.Should().BeNull("a non-[Sortable] AsDetail array carries no isSortable flag (absent, not false)");
    }

    [Fact]
    public void Sortable_attribute_on_a_non_AsDetail_array_property_is_ignored()
    {
        var ctx = new OrderedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        // [Sortable] on a scalar string — meaningless, must not set the flag.
        file.PersistentObject.Attributes.Single(a => a.Name == "Name").IsSortable.Should().BeNull();
    }

    [Fact]
    public void IsSortable_survives_re_synchronize()
    {
        var ctx = new OrderedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);
        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        file.PersistentObject.Attributes.Single(a => a.Name == "Steps").IsSortable.Should().BeTrue();
    }

    [Fact]
    public void MultiLineString_dataType_is_preserved_on_re_synchronize()
    {
        var ctx = new OrderedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        // Hand-edit: promote the plain string "Name" attribute to a MultiLineString (textarea) presentation.
        var path = ModelFile("MS_OrderedParent");
        var tampered = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(path),
            "(\"name\": \"Name\".*?\"dataType\": )\"string\"",
            "$1\"MultiLineString\"",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        File.WriteAllText(path, tampered);

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(path);
        file.PersistentObject.Attributes.Single(a => a.Name == "Name").DataType
            .Should().Be("MultiLineString", "a hand-set MultiLineString is a presentation override the synchronizer preserves across re-sync");
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
    public void Throws_on_authored_breadcrumb_referencing_an_unknown_attribute()
    {
        var ctx = new BadBreadcrumbContext();
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(ctx);
        var path = ModelFile("MS_BadBreadcrumb");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{FirstName}", "{Nope}"));

        var act = () => sync.SynchronizeModels(ctx);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown attribute*Nope*");
    }

    [Fact]
    public void Throws_on_authored_breadcrumb_with_unbalanced_braces()
    {
        var ctx = new UnbalancedBreadcrumbContext();
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(ctx);
        var path = ModelFile("MS_UnbalancedBreadcrumb");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"{FirstName}\"", "\"{FirstName\""));

        var act = () => sync.SynchronizeModels(ctx);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid breadcrumb template*");
    }

    [Fact]
    public void Synthesized_default_breadcrumb_prefers_a_Name_attribute_over_the_first_attribute()
    {
        var ctx = new NamedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        // Description is the first declared attribute, but Name is preferred for the default.
        Read<EntityTypeFile>(ModelFile("MS_NamedThing")).PersistentObject.Breadcrumb.Should().Be("{Name}");
    }

    // --- #273: JSON-authoritative templates + the property-level [Breadcrumb] marker ---

    [Fact]
    public void Synthesized_default_breadcrumb_prefers_the_marked_property()
    {
        var ctx = new MarkedThingContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        // The [Breadcrumb]-marked (computed, [IgnoreProperty]) member is the type's declared
        // breadcrumb value — the synthesized template names it, and validation must accept the
        // marked-ignored placeholder as the sanctioned shape.
        Read<EntityTypeFile>(ModelFile("MS_MarkedThing")).PersistentObject.Breadcrumb.Should().Be("{Crumb}");
    }

    [Fact]
    public void Authored_json_template_survives_re_synchronize()
    {
        var ctx = new SinglePersonContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);
        var path = ModelFile("MS_TestPerson");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{FirstName}", "{LastName}"));

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(path).PersistentObject.Breadcrumb.Should().Be("{LastName}",
            "the model JSON is the display authority; synchronize preserves authored templates");
    }

    [Fact]
    public void Drift_warning_when_the_authored_template_omits_the_marked_property()
    {
        var ctx = new MarkedThingContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);
        var path = ModelFile("MS_MarkedThing");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{Crumb}", "{FirstName}"));

        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            sync.SynchronizeModels(ctx);
        }
        finally
        {
            Console.SetOut(original);
        }

        // The authored template wins, but the drift — grid sorts by Crumb, display shows
        // FirstName — is warned about, since no gate can see it.
        Read<EntityTypeFile>(path).PersistentObject.Breadcrumb.Should().Be("{FirstName}");
        writer.ToString().Should().Contain("Crumb");
    }

    [Fact]
    public void Breadcrumb_projection_satisfiable_is_null_when_no_projection_type()
    {
        // No projection registered → the satisfiable flag is left null (renderable as-is).
        var ctx = new SinglePersonContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).PersistentObject.BreadcrumbProjectionSatisfiable.Should().BeNull();
    }

    [Fact]
    public void Breadcrumb_projection_satisfiable_is_false_when_a_placeholder_field_is_absent_from_the_projection()
    {
        // MS_BreadcrumbPerson breadcrumb is "{LastName}, {FirstName}", but the MS_TestVehicle
        // projection has neither field → the list path must batch-load the collection documents.
        var registration = new IndexRegistration
        {
            IndexName = "BcPeople_Index",
            IndexType = typeof(MS_BreadcrumbPerson),
            CollectionType = typeof(MS_BreadcrumbPerson),
            ProjectionType = typeof(MS_TestVehicle),
        };
        _indexRegistry.GetRegistrationForCollectionType(typeof(MS_BreadcrumbPerson)).Returns(registration);

        var ctx = new BreadcrumbContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_BreadcrumbPerson")).PersistentObject.BreadcrumbProjectionSatisfiable
            .Should().BeFalse();
    }

    [Fact]
    public void Breadcrumb_projection_satisfiable_is_null_when_every_placeholder_field_is_on_the_projection()
    {
        // The projection carries LastName and FirstName, so the breadcrumb renders from the
        // projection alone — satisfiable stays null (no collection-document load needed).
        var registration = new IndexRegistration
        {
            IndexName = "BcPeople_Index",
            IndexType = typeof(MS_BreadcrumbPerson),
            CollectionType = typeof(MS_BreadcrumbPerson),
            ProjectionType = typeof(MS_BreadcrumbPersonProjection),
        };
        _indexRegistry.GetRegistrationForCollectionType(typeof(MS_BreadcrumbPerson)).Returns(registration);

        var ctx = new BreadcrumbContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_BreadcrumbPerson")).PersistentObject.BreadcrumbProjectionSatisfiable
            .Should().BeNull();
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

    // --- [IgnoreProperty] (#254) ---

    [Fact]
    public void Ignored_property_is_excluded_from_generated_attributes()
    {
        var ctx = new IgnoredContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson"));
        file.PersistentObject.Attributes.Select(a => a.Name)
            .Should().BeEquivalentTo(["FirstName"], "[IgnoreProperty] excludes a property from the model");
    }

    [Fact]
    public void Re_synchronize_removes_an_attribute_that_has_become_ignored()
    {
        // The property was part of the model before it was ignored, so its attribute block is
        // sitting in a committed model file. Synchronize must delete it, not leave an orphan
        // that the mapper can never populate (a required orphan would deadlock every save).
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_IgnoredPerson"), """
            {"persistentObject":{"id":"11111111-1111-1111-1111-111111111111",
            "name":"MS_IgnoredPerson","clrType":"MintPlayer.Spark.Tests.Services.MS_IgnoredPerson",
            "attributes":[
              {"id":"22222222-2222-2222-2222-222222222222","name":"FirstName","dataType":"String"},
              {"id":"33333333-3333-3333-3333-333333333333","name":"InternalToken","dataType":"String"}
            ]}}
            """);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IgnoredContext());

        var file = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson"));
        file.PersistentObject.Attributes.Select(a => a.Name).Should().NotContain("InternalToken");
        file.PersistentObject.Attributes.Select(a => a.Name).Should().Contain("FirstName");
    }

    // --- attributes without a CLR property survive synchronize (#253 part 2) ---

    [Fact]
    public void Hand_authored_attribute_with_no_CLR_property_survives_with_every_field_intact()
    {
        // The motivating case: an attribute authored by hand and populated at runtime, never backed
        // by a property. Synchronize must not touch it. The id assertion carries the most weight —
        // clients key on it, so regenerating one silently rewrites identity.
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_IgnoredPerson"), """
            {"persistentObject":{"id":"11111111-1111-1111-1111-111111111111",
            "name":"MS_IgnoredPerson","clrType":"MintPlayer.Spark.Tests.Services.MS_IgnoredPerson",
            "attributes":[
              {"id":"22222222-2222-2222-2222-222222222222","name":"FirstName","dataType":"String"},
              {"id":"44444444-4444-4444-4444-444444444444","name":"TotalPurchaseBudget",
               "dataType":"Decimal","isReadOnly":true,"order":7,"columnSpan":2,
               "renderer":"currency","rendererOptions":{"symbol":"EUR"},
               "editMode":"never","label":{"en":"Total purchase budget"},
               "rules":[{"type":"range","value":"0,1000"}]}
            ]}}
            """);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IgnoredContext());

        var attrs = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson")).PersistentObject.Attributes;
        var virtualAttr = attrs.Should().ContainSingle(a => a.Name == "TotalPurchaseBudget").Subject;

        virtualAttr.Id.Should().Be(Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "a regenerated id silently breaks every client that stored the old one");
        virtualAttr.DataType.Should().Be("Decimal");
        virtualAttr.IsReadOnly.Should().BeTrue();
        virtualAttr.Order.Should().Be(7);
        virtualAttr.ColumnSpan.Should().Be(2);
        virtualAttr.Renderer.Should().Be("currency");
        virtualAttr.RendererOptions.Should().ContainKey("symbol");
        virtualAttr.EditMode.Should().Be("never");
        virtualAttr.Rules.Should().ContainSingle();

        attrs.Select(a => a.Name).Should().Contain("FirstName", "the real property is unaffected");
    }

    [Fact]
    public void Attribute_whose_property_was_removed_is_kept_rather_than_dropped()
    {
        // The rename/delete case. Previously this vanished along with its renderer, label,
        // translations and rules — a rename became delete-and-recreate-with-defaults, unlogged.
        // MS_IgnoredPerson has no 'Nickname' property, standing in for one that was removed.
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_IgnoredPerson"), """
            {"persistentObject":{"id":"11111111-1111-1111-1111-111111111111",
            "name":"MS_IgnoredPerson","clrType":"MintPlayer.Spark.Tests.Services.MS_IgnoredPerson",
            "attributes":[
              {"id":"22222222-2222-2222-2222-222222222222","name":"FirstName","dataType":"String"},
              {"id":"55555555-5555-5555-5555-555555555555","name":"Nickname","dataType":"String",
               "renderer":"badge"}
            ]}}
            """);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IgnoredContext());

        var attrs = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson")).PersistentObject.Attributes;
        attrs.Should().ContainSingle(a => a.Name == "Nickname")
            .Which.Renderer.Should().Be("badge", "hand-set fields ride along with the attribute");
    }

    [Fact]
    public void Ignored_property_still_removes_its_attribute_even_though_orphans_are_now_kept()
    {
        // The distinction the whole change hinges on. [IgnoreProperty] is an explicit instruction to
        // drop the attribute; a property that merely disappeared is not. Keeping both behaviours
        // separate is what stops this fix from silently resurrecting deliberately-ignored fields.
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_IgnoredPerson"), """
            {"persistentObject":{"id":"11111111-1111-1111-1111-111111111111",
            "name":"MS_IgnoredPerson","clrType":"MintPlayer.Spark.Tests.Services.MS_IgnoredPerson",
            "attributes":[
              {"id":"22222222-2222-2222-2222-222222222222","name":"FirstName","dataType":"String"},
              {"id":"33333333-3333-3333-3333-333333333333","name":"InternalToken","dataType":"String"},
              {"id":"55555555-5555-5555-5555-555555555555","name":"Nickname","dataType":"String"}
            ]}}
            """);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IgnoredContext());

        var names = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson")).PersistentObject.Attributes
            .Select(a => a.Name).ToArray();

        names.Should().NotContain("InternalToken", "[IgnoreProperty] removal stays destructive");
        names.Should().Contain("Nickname", "a property that merely vanished is a different case");
    }

    // --- get-only computed properties (#253) ---

    [Fact]
    public void Get_only_property_becomes_a_read_only_attribute()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new ComputedContext());

        var attrs = Read<EntityTypeFile>(ModelFile("MS_ComputedOrder")).PersistentObject.Attributes;

        var computed = attrs.Should().ContainSingle(a => a.Name == "Total").Subject;
        computed.IsReadOnly.Should().BeTrue("nothing can write a property with no setter");
        computed.DataType.Should().Be("decimal",
            "a computed property is typed from its return type like any other");
        computed.IsVisible.Should().BeTrue("read-only is not hidden");
        computed.IsRequired.Should().BeFalse(
            "a required attribute nothing can populate would block every save");

        attrs.Should().ContainSingle(a => a.Name == "Quantity")
            .Which.IsReadOnly.Should().BeFalse("a settable property is unaffected");
    }

    [Fact]
    public void Hand_set_IsReadOnly_survives_re_synchronize()
    {
        // IsReadOnly is only assigned when the attribute is created; the update branch leaves it
        // alone. Someone marking a settable property read-only in the JSON must keep that.
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_ComputedOrder"), """
            {"persistentObject":{"id":"11111111-1111-1111-1111-111111111111",
            "name":"MS_ComputedOrder","clrType":"MintPlayer.Spark.Tests.Services.MS_ComputedOrder",
            "attributes":[
              {"id":"22222222-2222-2222-2222-222222222222","name":"Quantity",
               "dataType":"Int32","isReadOnly":true}
            ]}}
            """);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new ComputedContext());

        Read<EntityTypeFile>(ModelFile("MS_ComputedOrder")).PersistentObject.Attributes
            .Should().ContainSingle(a => a.Name == "Quantity")
            .Which.IsReadOnly.Should().BeTrue("a hand-set value is not stomped by re-synchronize");
    }

    [Fact]
    public void An_indexer_does_not_become_an_attribute_named_Item()
    {
        // Reflection reports `this[int]` as a property named "Item". It needs an argument to
        // produce a value, so there is nothing an attribute could read.
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IndexerContext());

        Read<EntityTypeFile>(ModelFile("MS_IndexerEntity")).PersistentObject.Attributes
            .Select(a => a.Name)
            .Should().BeEquivalentTo(["Name"], "the indexer is not part of the model");
    }

    [Fact]
    public void Ignored_complex_property_does_not_produce_an_embedded_model_file()
    {
        // Discovery and attribute generation share the filter: an ignored property must not drag
        // its type into the model as an embedded type nothing references.
        var ctx = new IgnoredEmbeddedContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_IgnoredAudit")).Should().BeFalse();
        Read<EntityTypeFile>(ModelFile("MS_IgnoredHolder")).PersistentObject.Attributes
            .Select(a => a.Name).Should().BeEquivalentTo(["Title"]);
    }

    [Fact]
    public void Ignored_property_on_an_embedded_type_is_excluded_from_that_types_model()
    {
        var ctx = new EmbeddedChildContext();
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_IgnoredChild")).PersistentObject.Attributes
            .Select(a => a.Name).Should().BeEquivalentTo(["Label"], "the rule applies at every level, not just entity roots");
    }

    [Fact]
    public void Ignoring_a_property_on_the_entity_vetoes_the_same_name_on_the_projection()
    {
        // The two name sets are unioned, so an entity-side ignore has to veto a projection that
        // still declares the property — otherwise the exclusion silently does nothing.
        var registration = new IndexRegistration
        {
            IndexName = "IgnoredPeople_Index",
            IndexType = typeof(MS_IgnoredPerson),
            CollectionType = typeof(MS_IgnoredPerson),
            ProjectionType = typeof(MS_IgnoredPersonProjection),
        };
        _indexRegistry.GetRegistrationForCollectionType(typeof(MS_IgnoredPerson)).Returns(registration);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IgnoredContext());

        Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson")).PersistentObject.Attributes
            .Select(a => a.Name).Should().NotContain("InternalToken");
    }

    // --- showedOn is preserved on projected entities (#274) ---

    private void RegisterBookProjection(Type projectionType)
    {
        var registration = new IndexRegistration
        {
            IndexName = "Books_Index",
            IndexType = typeof(MS_ProjectedBook),
            CollectionType = typeof(MS_ProjectedBook),
            ProjectionType = projectionType,
        };
        _indexRegistry.GetRegistrationForCollectionType(typeof(MS_ProjectedBook)).Returns(registration);
    }

    private void TamperShowedOn(string entityName, string attributeName, string newValue)
    {
        var path = ModelFile(entityName);
        var tampered = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(path),
            $"(\"name\": \"{attributeName}\".*?\"showedOn\": )\"[^\"]+\"",
            $"$1\"{newValue}\"",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        File.WriteAllText(path, tampered);
    }

    private EntityAttributeDefinition BookAttribute(string name) =>
        Read<EntityTypeFile>(ModelFile("MS_ProjectedBook")).PersistentObject.Attributes
            .Single(a => a.Name == name);

    [Theory]
    [InlineData("PersistentObject", EShowedOn.PersistentObject)]
    [InlineData("Query", EShowedOn.Query)]
    public void Hand_trimmed_ShowedOn_on_a_dual_present_attribute_survives_re_synchronize(
        string trimmed, EShowedOn expected)
    {
        // The #274 repro: Title exists on both the entity and the projection, so its derived
        // capability is Query|PersistentObject. An author narrows it (e.g. to keep a load-bearing
        // but constant column off the generic grid); re-synchronize must not widen it back.
        RegisterBookProjection(typeof(MS_ProjectedBookView));
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new ProjectedBookContext());

        TamperShowedOn("MS_ProjectedBook", "Title", trimmed);

        sync.SynchronizeModels(new ProjectedBookContext());

        BookAttribute("Title").ShowedOn.Should().Be(expected,
            "showedOn is presentation; projection membership is the capability to show, not a mandate");
    }

    [Fact]
    public void Attribute_leaving_the_projection_loses_the_Query_flag()
    {
        // Structural narrowing must keep working: once Title is no longer on the projection, a
        // grid column for it could only ever render empty. Synchronize may remove sides that
        // structurally disappeared — it must just never add one back.
        RegisterBookProjection(typeof(MS_ProjectedBookView));
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new ProjectedBookContext());
        BookAttribute("Title").ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);

        RegisterBookProjection(typeof(MS_ProjectedBookViewWithoutTitle));
        sync.SynchronizeModels(new ProjectedBookContext());

        BookAttribute("Title").ShowedOn.Should().Be(EShowedOn.PersistentObject);
    }

    [Fact]
    public void ShowedOn_with_no_valid_side_self_heals_to_the_derived_capability()
    {
        // A hand-set "Query" on a collection-only attribute has no valid side left after the
        // intersection. Healing to the capability beats an empty flag set, which would make the
        // attribute permanently invisible everywhere.
        RegisterBookProjection(typeof(MS_ProjectedBookView));
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new ProjectedBookContext());

        TamperShowedOn("MS_ProjectedBook", "Secret", "Query");

        sync.SynchronizeModels(new ProjectedBookContext());

        BookAttribute("Secret").ShowedOn.Should().Be(EShowedOn.PersistentObject);
    }

    [Fact]
    public void Adding_a_projection_to_an_existing_entity_still_narrows_single_sided_attributes()
    {
        // The adoption path (#274's real-world trigger): the entity was synchronized long before
        // [GenerateIndex] was added, so every attribute stores the "both" default. The first sync
        // after the projection appears must still narrow single-sided attributes — their stored
        // value intersected with the new capability — while dual-present ones keep both flags.
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new ProjectedBookContext());
        BookAttribute("Secret").ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);

        RegisterBookProjection(typeof(MS_ProjectedBookView));
        sync.SynchronizeModels(new ProjectedBookContext());

        BookAttribute("Secret").ShowedOn.Should().Be(EShowedOn.PersistentObject,
            "not on the projection, so it can never render on the grid");
        BookAttribute("Title").ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);
        BookAttribute("AuthorName").ShowedOn.Should().Be(EShowedOn.Query,
            "projection-only, created by this run with the derived default");
    }

    [Fact]
    public void Plain_entity_ShowedOn_is_untouched_on_re_synchronize()
    {
        // No projection: the entity itself backs the query, every side is always capable, so the
        // authored value passes through verbatim.
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new SinglePersonContext());

        TamperShowedOn("MS_TestPerson", "FirstName", "PersistentObject");

        sync.SynchronizeModels(new SinglePersonContext());

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).PersistentObject.Attributes
            .Single(a => a.Name == "FirstName")
            .ShowedOn.Should().Be(EShowedOn.PersistentObject);
    }

    // --- #275: hand-set `query` on non-[Reference] attributes must survive synchronize ---

    /// <summary>Sets a field on one attribute object inside the model JSON, preserving the rest.</summary>
    private void TamperAttribute(string entityName, string attributeName, string field, string? value)
    {
        var path = ModelFile(entityName);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var attrs = root["persistentObject"]!["attributes"]!.AsArray();
        var attr = attrs.Single(a => a!["name"]!.GetValue<string>() == attributeName)!;
        attr[field] = value;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private EntityAttributeDefinition ReadAttribute(string entityName, string attributeName)
        => Read<EntityTypeFile>(ModelFile(entityName)).PersistentObject.Attributes
            .Single(a => a.Name == attributeName);

    [Fact]
    public void Hand_set_query_on_non_reference_attribute_survives_re_synchronize()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new SinglePersonContext());

        TamperAttribute("MS_TestPerson", "FirstName", "query", "GetPeople");

        sync.SynchronizeModels(new SinglePersonContext());

        ReadAttribute("MS_TestPerson", "FirstName").Query.Should().Be("GetPeople",
            "a query authored on a non-[Reference] attribute has no derivation source — only the author could have written it");

        // Fixed point: a further run must not change the file.
        var afterSecond = File.ReadAllText(ModelFile("MS_TestPerson"));
        sync.SynchronizeModels(new SinglePersonContext());
        File.ReadAllText(ModelFile("MS_TestPerson")).Should().Be(afterSecond);
    }

    [Fact]
    public void Removing_Reference_clears_the_stale_derived_query()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new SinglePersonContext());

        // Simulate the leftovers of a property that used to carry [Reference]: the stored query
        // was machine-derived, so it must be cleared, not preserved as if authored.
        TamperAttribute("MS_TestPerson", "FirstName", "dataType", "Reference");
        TamperAttribute("MS_TestPerson", "FirstName", "referenceType", typeof(MS_TestTag).FullName);
        TamperAttribute("MS_TestPerson", "FirstName", "query", "GetTags");

        sync.SynchronizeModels(new SinglePersonContext());

        var attr = ReadAttribute("MS_TestPerson", "FirstName");
        attr.Query.Should().BeNull("the stored query was derived from the removed [Reference]");
        attr.ReferenceType.Should().BeNull();
        attr.DataType.Should().Be("string");
    }

    [Fact]
    public void Reference_attribute_query_is_still_rederived_on_every_run()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new TaggedContext());

        TamperAttribute("MS_TestTagged", "TagIds", "query", "GetWrong");

        sync.SynchronizeModels(new TaggedContext());

        ReadAttribute("MS_TestTagged", "TagIds").Query.Should().Be("GetTags",
            "a [Reference] attribute's query has a derivation source and is structural — it re-derives");
    }

    [Fact]
    public void Breadcrumb_referencing_an_ignored_property_fails_with_an_explanatory_message()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(new IgnoredBreadcrumbContext());
        var path = ModelFile("MS_IgnoredBreadcrumb");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{FirstName}", "{InternalToken}"));

        var act = () => sync.SynchronizeModels(new IgnoredBreadcrumbContext());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IgnoreProperty*",
                "the failure must name the cause, not just report an unknown attribute");
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

// AsDetail child for the [Sortable] fixtures.
public class MS_Step
{
    public string Label { get; set; } = string.Empty;
}

public class MS_OrderedParent
{
    public string? Id { get; set; }

    [Sortable]                                  // AsDetail array + [Sortable] → IsSortable: true
    public List<MS_Step> Steps { get; set; } = [];

    public List<MS_Step> Notes { get; set; } = []; // AsDetail array, no [Sortable] → null

    [Sortable]                                  // scalar + [Sortable] → ignored (null)
    public string Name { get; set; } = string.Empty;
}

public class MS_BreadcrumbPerson
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

// Projection that DOES carry the breadcrumb placeholder fields (LastName, FirstName).
public class MS_BreadcrumbPersonProjection
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

// #273: the property-level marker in its sanctioned computed + [IgnoreProperty] form.
public class MS_MarkedThing
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;

    [Breadcrumb, IgnoreProperty]
    public string Crumb => FirstName.ToUpperInvariant();
}

// First attribute is Description, but a Name attribute exists and is preferred for the default breadcrumb.
public class MS_NamedThing
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class MS_BadBreadcrumb
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
}

public class MS_UnbalancedBreadcrumb
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
}

// --- [IgnoreProperty] fixtures (#254) ---

public class MS_IgnoredPerson
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;

    [IgnoreProperty]
    public string InternalToken { get; set; } = string.Empty;
}

// Projection that still declares InternalToken — the entity-side ignore must veto it.
public class MS_IgnoredPersonProjection
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string InternalToken { get; set; } = string.Empty;
}

public class MS_IgnoredAudit
{
    public string? Id { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
}

public class MS_IgnoredHolder
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;

    [IgnoreProperty]
    public MS_IgnoredAudit? Audit { get; set; }
}

// Embedded child carrying its own ignored property.
public class MS_IgnoredChild
{
    public string? Id { get; set; }
    public string Label { get; set; } = string.Empty;

    [IgnoreProperty]
    public string Scratch { get; set; } = string.Empty;
}

public class MS_EmbeddedChildParent
{
    public string? Id { get; set; }
    public MS_IgnoredChild? Child { get; set; }
}

public class MS_IgnoredBreadcrumb
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;

    [IgnoreProperty]
    public string InternalToken { get; set; } = string.Empty;
}

public class IgnoredContext : SparkContext
{
    public IRavenQueryable<MS_IgnoredPerson> People => Session.Query<MS_IgnoredPerson>();
}

// --- showedOn preservation fixtures (#274) ---

public class MS_ProjectedBook
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;   // dual-present: also on the projection
    public string Secret { get; set; } = string.Empty;  // collection-only
}

public class MS_ProjectedBookView
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty; // projection-only
}

// Stand-in for a projection that dropped Title (e.g. [IgnoreForIndex] added later).
public class MS_ProjectedBookViewWithoutTitle
{
    public string? Id { get; set; }
    public string AuthorName { get; set; } = string.Empty;
}

public class ProjectedBookContext : SparkContext
{
    public IRavenQueryable<MS_ProjectedBook> Books => Session.Query<MS_ProjectedBook>();
}

// --- get-only computed property fixtures (#253) ---

public class MS_ComputedOrder
{
    public string? Id { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>Get-only: the case that was invisible to the model before #253.</summary>
    public decimal Total => Quantity * UnitPrice;
}

public class ComputedContext : SparkContext
{
    public IRavenQueryable<MS_ComputedOrder> Orders => Session.Query<MS_ComputedOrder>();
}

public class MS_IndexerEntity
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;

    private readonly Dictionary<string, string> _bag = [];

    /// <summary>Reflection reports this as a property named "Item" (#253).</summary>
    public string this[string key]
    {
        get => _bag.TryGetValue(key, out var v) ? v : string.Empty;
        set => _bag[key] = value;
    }
}

public class IndexerContext : SparkContext
{
    public IRavenQueryable<MS_IndexerEntity> Entities => Session.Query<MS_IndexerEntity>();
}

public class IgnoredEmbeddedContext : SparkContext
{
    public IRavenQueryable<MS_IgnoredHolder> Holders => Session.Query<MS_IgnoredHolder>();
}

public class EmbeddedChildContext : SparkContext
{
    public IRavenQueryable<MS_EmbeddedChildParent> Parents => Session.Query<MS_EmbeddedChildParent>();
}

public class IgnoredBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_IgnoredBreadcrumb> Items => Session.Query<MS_IgnoredBreadcrumb>();
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

public class NamedContext : SparkContext
{
    public IRavenQueryable<MS_NamedThing> Things => Session.Query<MS_NamedThing>();
}

public class OrderedContext : SparkContext
{
    public IRavenQueryable<MS_OrderedParent> Parents => Session.Query<MS_OrderedParent>();
}

public class MarkedThingContext : SparkContext
{
    public IRavenQueryable<MS_MarkedThing> Things => Session.Query<MS_MarkedThing>();
}

public class BadBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_BadBreadcrumb> Items => Session.Query<MS_BadBreadcrumb>();
}

public class UnbalancedBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_UnbalancedBreadcrumb> Items => Session.Query<MS_UnbalancedBreadcrumb>();
}
