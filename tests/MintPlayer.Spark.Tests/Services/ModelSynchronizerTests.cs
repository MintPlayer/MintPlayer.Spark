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
    private readonly IIndexCatalog _indexCatalog = Substitute.For<IIndexCatalog>();
    private readonly string _modelPath;

    public ModelSynchronizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-modelsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _modelPath = Path.Combine(_tempDir, "App_Data", "Model");
        _hostEnv.ContentRootPath.Returns(_tempDir);
        _indexCatalog.GetAllEntries().Returns([]);
        _indexCatalog.GetDefaultForCollectionType(Arg.Any<Type>()).Returns((IndexCatalogEntry?)null);
        _indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ModelSynchronizer CreateSynchronizer() => new(_hostEnv, _indexCatalog);

    private string ModelFile(string entityName) => Path.Combine(_modelPath, $"{entityName}.json");

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Creates_Model_directory_when_missing_even_if_context_has_no_queries()
    {
        var ctx = typeof(EmptyContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Directory.Exists(_modelPath).Should().BeTrue();
        Directory.GetFiles(_modelPath).Should().BeEmpty();
    }

    [Fact]
    public void Writes_one_JSON_file_per_IRavenQueryable_property()
    {
        var ctx = typeof(TwoEntityContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_TestPerson")).Should().BeTrue();
        File.Exists(ModelFile("MS_TestCar")).Should().BeTrue();
    }

    [Fact]
    public void Writes_PersistentObject_with_ClrType_Name_and_default_query()
    {
        var ctx = typeof(SinglePersonContext);
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
        var ctx = typeof(SinglePersonContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));
        // FirstName, LastName, Age — Id is excluded.
        file.PersistentObject.Attributes.Select(a => a.Name).Should().BeEquivalentTo(["FirstName", "LastName", "Age"]);
    }

    [Fact]
    public void Preserves_existing_PersistentObject_id_and_attribute_ids_on_re_synchronize()
    {
        var ctx = typeof(SinglePersonContext);
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
        var ctx = typeof(TaggedContext);
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
        var ctx = typeof(TaggedContext);
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
        var ctx = typeof(OrderedContext);
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
        var ctx = typeof(OrderedContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        var notes = file.PersistentObject.Attributes.Single(a => a.Name == "Notes");
        notes.DataType.Should().Be("AsDetail");
        notes.IsArray.Should().BeTrue();
        notes.IsSortable.Should().NotHaveValue("a non-[Sortable] AsDetail array carries no isSortable flag (absent, not false)");
    }

    [Fact]
    public void Sortable_attribute_on_a_non_AsDetail_array_property_is_ignored()
    {
        var ctx = typeof(OrderedContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        // [Sortable] on a scalar string — meaningless, must not set the flag.
        file.PersistentObject.Attributes.Single(a => a.Name == "Name").IsSortable.Should().NotHaveValue();
    }

    [Fact]
    public void IsSortable_survives_re_synchronize()
    {
        var ctx = typeof(OrderedContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);
        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_OrderedParent"));
        file.PersistentObject.Attributes.Single(a => a.Name == "Steps").IsSortable.Should().BeTrue();
    }

    /// <summary>
    /// A CLR <c>string</c> property can be a paragraph, a link, or the address of an image, and the
    /// type says nothing about which — so these are hand-authored and must survive re-synchronize.
    /// <c>MultiLineString</c> was a one-off condition in the synchronizer; #327 §9.1 added
    /// <c>image</c> and <c>url</c>, and the list moved to <c>SparkStringPresentations</c> so a
    /// second hard-coded name could not start disagreeing with what the client renders.
    /// </summary>
    [Theory]
    [InlineData("MultiLineString")]
    [InlineData("image")]
    [InlineData("url")]
    public void A_string_presentation_override_is_preserved_on_re_synchronize(string presentation)
    {
        var ctx = typeof(OrderedContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        // Hand-edit: promote the plain string "Name" attribute to the presentation under test.
        var path = ModelFile("MS_OrderedParent");
        File.WriteAllText(path, RetypeNameAttribute(File.ReadAllText(path), presentation));

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(path);
        file.PersistentObject.Attributes.Single(a => a.Name == "Name").DataType
            .Should().Be(presentation,
                "a hand-set presentation override is preserved across re-sync — the CLR shape cannot express it");
    }

    [Fact]
    public void Every_declared_string_presentation_is_covered_by_the_theory_above()
    {
        // A tripwire, not a tautology: adding a fourth presentation to SparkStringPresentations
        // without a matching InlineData would ship an override the synchronizer silently resets on
        // the next sync, and nothing else in the suite would notice.
        SparkStringPresentations.All.Should().BeEquivalentTo(["MultiLineString", "image", "url"]);
    }

    [Fact]
    public void A_presentation_override_is_dropped_when_the_property_stops_being_a_string()
    {
        // The other half of the rule. Preservation is conditional on the CLR shape still being a
        // string; a hint describing something that is no longer true is worse than no hint, and it
        // is part of the structural hash, so verification would go on confirming the stale value.
        SparkStringPresentations.Preserves("image", "string").Should().BeTrue();
        SparkStringPresentations.Preserves("image", "datetime").Should().BeFalse();
        SparkStringPresentations.Preserves("Reference", "string").Should().BeFalse("only presentation overrides survive");
        SparkStringPresentations.Preserves(null, "string").Should().BeFalse();
    }

    private static string RetypeNameAttribute(string json, string dataType)
        => System.Text.RegularExpressions.Regex.Replace(
            json,
            "(\"name\": \"Name\".*?\"dataType\": )\"string\"",
            $"$1\"{dataType}\"",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]
    public void Synthesizes_a_default_breadcrumb_from_the_first_attribute_when_none_authored()
    {
        var ctx = typeof(SinglePersonContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_TestPerson"));
        // No Name/FullName/Title, no [Breadcrumb] → first attribute (FirstName).
        file.PersistentObject.Breadcrumb.Should().Be("{FirstName}");
    }

    [Fact]
    public void Throws_on_authored_breadcrumb_referencing_an_unknown_attribute()
    {
        var ctx = typeof(BadBreadcrumbContext);
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
        var ctx = typeof(UnbalancedBreadcrumbContext);
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
        var ctx = typeof(NamedContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        // Description is the first declared attribute, but Name is preferred for the default.
        Read<EntityTypeFile>(ModelFile("MS_NamedThing")).PersistentObject.Breadcrumb.Should().Be("{Name}");
    }

    // --- #273: JSON-authoritative templates + the property-level [Breadcrumb] marker ---

    [Fact]
    public void Synthesized_default_breadcrumb_prefers_the_marked_property()
    {
        var ctx = typeof(MarkedThingContext);
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
        var ctx = typeof(SinglePersonContext);
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
        var ctx = typeof(MarkedThingContext);
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
        var ctx = typeof(SinglePersonContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).PersistentObject.BreadcrumbProjectionSatisfiable.Should().NotHaveValue();
    }

    [Fact]
    public void Breadcrumb_projection_satisfiable_is_false_when_a_placeholder_field_is_absent_from_the_projection()
    {
        // MS_BreadcrumbPerson breadcrumb is "{LastName}, {FirstName}", but the MS_TestVehicle
        // projection has neither field → the list path must batch-load the collection documents.
        var entry = new IndexCatalogEntry
        {
            IndexName = "BcPeople_Index",
            IndexType = typeof(MS_BreadcrumbPerson),
            CollectionType = typeof(MS_BreadcrumbPerson),
            ProjectionType = typeof(MS_TestVehicle),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_BreadcrumbPerson)).Returns(entry);

        var ctx = typeof(BreadcrumbContext);
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
        var entry = new IndexCatalogEntry
        {
            IndexName = "BcPeople_Index",
            IndexType = typeof(MS_BreadcrumbPerson),
            CollectionType = typeof(MS_BreadcrumbPerson),
            ProjectionType = typeof(MS_BreadcrumbPersonProjection),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_BreadcrumbPerson)).Returns(entry);

        var ctx = typeof(BreadcrumbContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_BreadcrumbPerson")).PersistentObject.BreadcrumbProjectionSatisfiable
            .Should().NotHaveValue();
    }

    [Fact]
    public void Removes_stale_projection_model_files_listed_in_the_catalog()
    {
        // Pre-create a stale Vehicle.json model file. Then register a projection that maps
        // collection MS_TestCar → projection MS_TestVehicle. Synchronize must delete Vehicle.json.
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MS_TestVehicle"), """{"persistentObject":{"id":"00000000-0000-0000-0000-000000000000","name":"MS_TestVehicle","clrType":"X"}}""");

        var entry = new IndexCatalogEntry
        {
            IndexName = "Cars_Index",
            IndexType = typeof(MS_TestCar),
            CollectionType = typeof(MS_TestCar),
            ProjectionType = typeof(MS_TestVehicle),
        };
        _indexCatalog.GetAllEntries().Returns([entry]);

        var ctx = typeof(EmptyContext);
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
        // Projection-ness comes from [FromIndex] on MS_TestVehicle (#279), not a registry stub.
        var ctx = typeof(ProjectionOnlyContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_TestVehicle")).Should().BeFalse();
    }

    // --- [IgnoreProperty] (#254) ---

    [Fact]
    public void Ignored_property_is_excluded_from_generated_attributes()
    {
        var ctx = typeof(IgnoredContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        var file = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson"));
        file.PersistentObject.Attributes.Select(a => a.Name)
            .Should().BeEquivalentTo(["FirstName"], because: "[IgnoreProperty] excludes a property from the model");
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
        sync.SynchronizeModels(typeof(IgnoredContext));

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
        sync.SynchronizeModels(typeof(IgnoredContext));

        var attrs = Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson")).PersistentObject.Attributes;
        var virtualAttr = attrs.Should().ContainSingle(a => a.Name == "TotalPurchaseBudget").Which;

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
        sync.SynchronizeModels(typeof(IgnoredContext));

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
        sync.SynchronizeModels(typeof(IgnoredContext));

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
        sync.SynchronizeModels(typeof(ComputedContext));

        var attrs = Read<EntityTypeFile>(ModelFile("MS_ComputedOrder")).PersistentObject.Attributes;

        var computed = attrs.Should().ContainSingle(a => a.Name == "Total").Which;
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
        sync.SynchronizeModels(typeof(ComputedContext));

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
        sync.SynchronizeModels(typeof(IndexerContext));

        Read<EntityTypeFile>(ModelFile("MS_IndexerEntity")).PersistentObject.Attributes
            .Select(a => a.Name)
            .Should().BeEquivalentTo(["Name"], because: "the indexer is not part of the model");
    }

    [Fact]
    public void Ignored_complex_property_does_not_produce_an_embedded_model_file()
    {
        // Discovery and attribute generation share the filter: an ignored property must not drag
        // its type into the model as an embedded type nothing references.
        var ctx = typeof(IgnoredEmbeddedContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        File.Exists(ModelFile("MS_IgnoredAudit")).Should().BeFalse();
        Read<EntityTypeFile>(ModelFile("MS_IgnoredHolder")).PersistentObject.Attributes
            .Select(a => a.Name).Should().BeEquivalentTo(["Title"]);
    }

    [Fact]
    public void Ignored_property_on_an_embedded_type_is_excluded_from_that_types_model()
    {
        var ctx = typeof(EmbeddedChildContext);
        var sync = CreateSynchronizer();

        sync.SynchronizeModels(ctx);

        Read<EntityTypeFile>(ModelFile("MS_IgnoredChild")).PersistentObject.Attributes
            .Select(a => a.Name).Should().BeEquivalentTo(["Label"], because: "the rule applies at every level, not just entity roots");
    }

    [Fact]
    public void Ignoring_a_property_on_the_entity_vetoes_the_same_name_on_the_projection()
    {
        // The two name sets are unioned, so an entity-side ignore has to veto a projection that
        // still declares the property — otherwise the exclusion silently does nothing.
        var entry = new IndexCatalogEntry
        {
            IndexName = "IgnoredPeople_Index",
            IndexType = typeof(MS_IgnoredPerson),
            CollectionType = typeof(MS_IgnoredPerson),
            ProjectionType = typeof(MS_IgnoredPersonProjection),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_IgnoredPerson)).Returns(entry);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(IgnoredContext));

        Read<EntityTypeFile>(ModelFile("MS_IgnoredPerson")).PersistentObject.Attributes
            .Select(a => a.Name).Should().NotContain("InternalToken");
    }

    // --- showedOn is preserved on projected entities (#274) ---

    private void RegisterBookProjection(Type projectionType)
    {
        var entry = new IndexCatalogEntry
        {
            IndexName = "Books_Index",
            IndexType = typeof(MS_ProjectedBook),
            CollectionType = typeof(MS_ProjectedBook),
            ProjectionType = projectionType,
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_ProjectedBook)).Returns(entry);
        _indexCatalog.GetByIndexName("Books_Index").Returns(entry);
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
        sync.SynchronizeModels(typeof(ProjectedBookContext));

        TamperShowedOn("MS_ProjectedBook", "Title", trimmed);

        sync.SynchronizeModels(typeof(ProjectedBookContext));

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
        sync.SynchronizeModels(typeof(ProjectedBookContext));
        BookAttribute("Title").ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);

        RegisterBookProjection(typeof(MS_ProjectedBookViewWithoutTitle));
        sync.SynchronizeModels(typeof(ProjectedBookContext));

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
        sync.SynchronizeModels(typeof(ProjectedBookContext));

        TamperShowedOn("MS_ProjectedBook", "Secret", "Query");

        sync.SynchronizeModels(typeof(ProjectedBookContext));

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
        sync.SynchronizeModels(typeof(ProjectedBookContext));
        BookAttribute("Secret").ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);

        RegisterBookProjection(typeof(MS_ProjectedBookView));
        sync.SynchronizeModels(typeof(ProjectedBookContext));

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
        sync.SynchronizeModels(typeof(SinglePersonContext));

        TamperShowedOn("MS_TestPerson", "FirstName", "PersistentObject");

        sync.SynchronizeModels(typeof(SinglePersonContext));

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
        sync.SynchronizeModels(typeof(SinglePersonContext));

        TamperAttribute("MS_TestPerson", "FirstName", "query", "GetPeople");

        sync.SynchronizeModels(typeof(SinglePersonContext));

        ReadAttribute("MS_TestPerson", "FirstName").Query.Should().Be("GetPeople",
            "a query authored on a non-[Reference] attribute has no derivation source — only the author could have written it");

        // Fixed point: a further run must not change the file.
        var afterSecond = File.ReadAllText(ModelFile("MS_TestPerson"));
        sync.SynchronizeModels(typeof(SinglePersonContext));
        File.ReadAllText(ModelFile("MS_TestPerson")).Should().Be(afterSecond);
    }

    [Fact]
    public void Removing_Reference_clears_the_stale_derived_query()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));

        // Simulate the leftovers of a property that used to carry [Reference]: the stored query
        // was machine-derived, so it must be cleared, not preserved as if authored.
        TamperAttribute("MS_TestPerson", "FirstName", "dataType", "Reference");
        TamperAttribute("MS_TestPerson", "FirstName", "referenceType", typeof(MS_TestTag).FullName);
        TamperAttribute("MS_TestPerson", "FirstName", "query", "GetTags");

        sync.SynchronizeModels(typeof(SinglePersonContext));

        var attr = ReadAttribute("MS_TestPerson", "FirstName");
        attr.Query.Should().BeNull("the stored query was derived from the removed [Reference]");
        attr.ReferenceType.Should().BeNull();
        attr.DataType.Should().Be("string");
    }

    [Fact]
    public void Reference_attribute_query_is_still_rederived_on_every_run()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(TaggedContext));

        TamperAttribute("MS_TestTagged", "TagIds", "query", "GetWrong");

        sync.SynchronizeModels(typeof(TaggedContext));

        ReadAttribute("MS_TestTagged", "TagIds").Query.Should().Be("GetTags",
            "a [Reference] attribute's query has a derivation source and is structural — it re-derives");
    }

    // --- #276: SparkQuery source staleness after a context property rename ---

    private void TamperQuery(string entityName, string queryName, string field, string? value)
    {
        var path = ModelFile(entityName);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var queries = root["queries"]!.AsArray();
        var query = queries.Single(q => q!["name"]!.GetValue<string>() == queryName)!;
        query[field] = value;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void AddQuery(string entityName, string queryName, string source)
    {
        var path = ModelFile(entityName);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        root["queries"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = queryName,
            ["source"] = source,
        });
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Renamed_context_property_retargets_the_existing_query_instead_of_minting_a_duplicate()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(RenameV1Context));

        var before = Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single();
        before.Source.Should().Be("Database.People");
        TamperQuery("MS_TestPerson", "GetPeople", "alias", "hand-authored-alias");

        sync.SynchronizeModels(typeof(RenameV2Context));

        var queries = Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries;
        queries.Should().ContainSingle("a rename must retarget in place, not leave a dead query plus a duplicate");
        var query = queries[0];
        query.Source.Should().Be("Database.Persons");
        query.Id.Should().Be(before.Id, "program units reference queries by id");
        query.Name.Should().Be("GetPersons", "a conventionally-named query follows the rename");
        query.Alias.Should().Be("hand-authored-alias", "authoring on the query survives the retarget");

        // Fixed point.
        var afterSecond = File.ReadAllText(ModelFile("MS_TestPerson"));
        sync.SynchronizeModels(typeof(RenameV2Context));
        File.ReadAllText(ModelFile("MS_TestPerson")).Should().Be(afterSecond);
    }

    [Fact]
    public void Unpairable_dead_Database_source_is_kept_and_warned()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));
        AddQuery("MS_TestPerson", "GetGhosts", "Database.Ghosts");

        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            sync.SynchronizeModels(typeof(SinglePersonContext));
        }
        finally
        {
            Console.SetOut(original);
        }

        var queries = Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries;
        queries.Should().HaveCount(2, "synchronize adds and modifies; it does not delete");
        queries.Single(q => q.Name == "GetGhosts").Source.Should().Be("Database.Ghosts");
        writer.ToString().Should().Contain("Ghosts", "a dead source silently returns no rows — it must be warned about");
    }

    [Fact]
    public void Custom_source_queries_pass_through_untouched()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));
        AddQuery("MS_TestPerson", "GetTop", "Custom.GetTop");

        sync.SynchronizeModels(typeof(SinglePersonContext));
        var afterSecond = File.ReadAllText(ModelFile("MS_TestPerson"));
        sync.SynchronizeModels(typeof(SinglePersonContext));

        File.ReadAllText(ModelFile("MS_TestPerson")).Should().Be(afterSecond);
        var top = Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single(q => q.Name == "GetTop");
        top.Source.Should().Be("Custom.GetTop");
    }

    [Fact]
    public void Ambiguous_multi_rename_falls_back_to_warn_and_mint()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(MultiRenameV1Context));

        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            sync.SynchronizeModels(typeof(MultiRenameV2Context));
        }
        finally
        {
            Console.SetOut(original);
        }

        // Two same-typed properties renamed at once cannot be paired — never guess. Old queries
        // stay (warned), new ones are minted.
        var queries = Read<EntityTypeFile>(ModelFile("MS_TestCar")).Queries;
        queries.Select(q => q.Name).Should().BeEquivalentTo(
            ["GetCars", "GetArchivedCars", "GetVehicles", "GetArchivedVehicles"]);
        writer.ToString().Should().Contain("Cars");
    }

    // --- Query indexName stamping + provenance (#279) ---

    [Fact]
    public void Minted_query_is_stamped_with_the_default_indexName()
    {
        var entry = new IndexCatalogEntry
        {
            IndexName = "People_Overview",
            IndexType = typeof(MS_TestPerson),
            CollectionType = typeof(MS_TestPerson),
            ProjectionType = typeof(MS_BreadcrumbPersonProjection),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_TestPerson)).Returns(entry);
        _indexCatalog.GetByIndexName("People_Overview").Returns(entry);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single().IndexName
            .Should().Be("People_Overview");
    }

    [Fact]
    public void An_unstamped_query_gains_the_default_indexName_on_the_next_synchronize()
    {
        // A pre-#279 model has queries without indexName. Empty is machine domain: the runtime
        // fell back to the entity file's binding, which is the default — stamping makes it explicit.
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));

        var entry = new IndexCatalogEntry
        {
            IndexName = "People_Overview",
            IndexType = typeof(MS_TestPerson),
            CollectionType = typeof(MS_TestPerson),
            ProjectionType = typeof(MS_BreadcrumbPersonProjection),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_TestPerson)).Returns(entry);
        _indexCatalog.GetByIndexName("People_Overview").Returns(entry);

        sync.SynchronizeModels(typeof(SinglePersonContext));

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single().IndexName
            .Should().Be("People_Overview");
    }

    [Fact]
    public void Hand_authored_indexName_naming_a_known_index_is_preserved()
    {
        // A deliberate binding to a non-default index is authoring; synchronize must not
        // retarget it to the default.
        var defaultEntry = new IndexCatalogEntry
        {
            IndexName = "People_Overview",
            IndexType = typeof(MS_TestPerson),
            CollectionType = typeof(MS_TestPerson),
            ProjectionType = typeof(MS_BreadcrumbPersonProjection),
        };
        var searchEntry = new IndexCatalogEntry
        {
            IndexName = "People_Search",
            IndexType = typeof(MS_TestPerson),
            CollectionType = typeof(MS_TestPerson),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_TestPerson)).Returns(defaultEntry);
        _indexCatalog.GetByIndexName("People_Overview").Returns(defaultEntry);
        _indexCatalog.GetByIndexName("People_Search").Returns(searchEntry);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));
        TamperQuery("MS_TestPerson", "GetPeople", "indexName", "People_Search");

        sync.SynchronizeModels(typeof(SinglePersonContext));

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single().IndexName
            .Should().Be("People_Search", "a binding to a known index is authored, not machine-owned");
    }

    [Fact]
    public void A_dead_indexName_is_retargeted_to_the_default_with_a_note()
    {
        // The named index no longer exists (renamed or removed). Failing would leave the model
        // unrepairable by the very command that repairs models — retarget with a console note.
        var entry = new IndexCatalogEntry
        {
            IndexName = "People_Overview",
            IndexType = typeof(MS_TestPerson),
            CollectionType = typeof(MS_TestPerson),
            ProjectionType = typeof(MS_BreadcrumbPersonProjection),
        };
        _indexCatalog.GetDefaultForCollectionType(typeof(MS_TestPerson)).Returns(entry);
        _indexCatalog.GetByIndexName("People_Overview").Returns(entry);

        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));
        TamperQuery("MS_TestPerson", "GetPeople", "indexName", "People_Gone");

        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            sync.SynchronizeModels(typeof(SinglePersonContext));
        }
        finally
        {
            Console.SetOut(original);
        }

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single().IndexName
            .Should().Be("People_Overview");
        writer.ToString().Should().Contain("People_Gone");
    }

    [Fact]
    public void A_dead_indexName_is_cleared_when_the_entity_has_no_default()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(SinglePersonContext));
        TamperQuery("MS_TestPerson", "GetPeople", "indexName", "People_Gone");

        sync.SynchronizeModels(typeof(SinglePersonContext));

        Read<EntityTypeFile>(ModelFile("MS_TestPerson")).Queries.Single().IndexName
            .Should().BeNull();
    }

    [Fact]
    public void Breadcrumb_referencing_an_ignored_property_fails_with_an_explanatory_message()
    {
        var sync = CreateSynchronizer();
        sync.SynchronizeModels(typeof(IgnoredBreadcrumbContext));
        var path = ModelFile("MS_IgnoredBreadcrumb");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{FirstName}", "{InternalToken}"));

        var act = () => sync.SynchronizeModels(typeof(IgnoredBreadcrumbContext));

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

// [FromIndex] is what makes this a projection (#279) — the fixture index type is a token; the
// synchronizer only tests attribute presence.
[FromIndex(typeof(MS_TestVehicleIndex))]
public class MS_TestVehicle
{
    public string? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class MS_TestVehicleIndex;

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

// #276: rename-retarget fixtures — the same entity exposed under an old and a new property name.
public class RenameV1Context : SparkContext
{
    public IRavenQueryable<MS_TestPerson> People => Session.Query<MS_TestPerson>();
}

public class RenameV2Context : SparkContext
{
    public IRavenQueryable<MS_TestPerson> Persons => Session.Query<MS_TestPerson>();
}

public class MultiRenameV1Context : SparkContext
{
    public IRavenQueryable<MS_TestCar> Cars => Session.Query<MS_TestCar>();
    public IRavenQueryable<MS_TestCar> ArchivedCars => Session.Query<MS_TestCar>();
}

public class MultiRenameV2Context : SparkContext
{
    public IRavenQueryable<MS_TestCar> Vehicles => Session.Query<MS_TestCar>();
    public IRavenQueryable<MS_TestCar> ArchivedVehicles => Session.Query<MS_TestCar>();
}

public class BadBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_BadBreadcrumb> Items => Session.Query<MS_BadBreadcrumb>();
}

public class UnbalancedBreadcrumbContext : SparkContext
{
    public IRavenQueryable<MS_UnbalancedBreadcrumb> Items => Session.Query<MS_UnbalancedBreadcrumb>();
}
