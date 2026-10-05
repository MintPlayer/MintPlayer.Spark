using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Linq;
using static MintPlayer.Spark.Tests.Builder.SparkExtensionsTests;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// The rejection side of <c>--spark-verify-model</c>, the merge-queue gate. Each model-sanity check
/// is reached by planting a hand-authored model file next to a synchronized one and asserting on the
/// message, because every check and the hash comparison all exit 3.
/// </summary>
/// <remarks>
/// Planted types are JSON-only (no <c>clrType</c>), so a synchronize after planting re-stamps the
/// hash over them without rewriting them. Re-synchronizing after the plant is what makes a failure
/// attributable to the check under test: with the hash in sync, the only thing left to exit 3 is
/// the check, and the run says so ("The model hash itself is unchanged").
/// </remarks>
[Collection(ProcessExitCodeCollection.Name)]
public class SparkModelVerifyChecksTests
{
    private const string HashUnchanged = "The model hash itself is unchanged";

    // --- the shape of a planted model file --------------------------------

    private static JsonObject Attribute(string name, string showedOn = "Query, PersistentObject", bool isArray = false,
        bool? canSort = null, string? triggersRefresh = null, string dataType = "string", string? lookupReferenceType = null)
    {
        var attribute = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = name,
            ["dataType"] = dataType,
            ["isArray"] = isArray,
            ["showedOn"] = showedOn,
        };
        if (canSort is not null) attribute["canSort"] = canSort;
        if (triggersRefresh is not null) attribute["triggersRefresh"] = triggersRefresh;
        if (lookupReferenceType is not null) attribute["lookupReferenceType"] = lookupReferenceType;
        return attribute;
    }

    private static JsonObject Query(string name, string source, string? alias = null) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["name"] = name,
        ["source"] = source,
        ["alias"] = alias,
    };

    private static void Plant(ScratchContentRoot scratch, string typeName, JsonObject[] attributes,
        JsonObject[]? queries = null, string[]? subQueries = null)
    {
        var file = new JsonObject
        {
            ["persistentObject"] = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["name"] = typeName,
                ["attributes"] = new JsonArray([.. attributes]),
                ["queries"] = new JsonArray([.. (subQueries ?? []).Select(s => (JsonNode)JsonValue.Create(s))]),
            },
            ["queries"] = new JsonArray([.. queries ?? []]),
        };
        File.WriteAllText(Path.Combine(scratch.ModelPath, typeName + ".json"), file.ToJsonString());
    }

    /// <summary>Synchronize, plant, re-stamp the hash, verify.</summary>
    private static (int ExitCode, string Reported) VerifyWith(ScratchContentRoot scratch, Action plant)
    {
        Synchronized(scratch);
        plant();
        Synchronized(scratch);
        return SparkExtensionsTests.Verify(scratch);
    }

    // --- refresh triggers --------------------------------------------------

    [Fact]
    public void A_refresh_trigger_on_a_type_with_no_actions_class_exits_3()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifyUnrefreshedProbe", [Attribute("Title", triggersRefresh: "Auto")]));

        exitCode.Should().Be(3);
        reported.Should().Contain("declares refresh triggers that nothing implements");
        reported.Should().Contain("VerifyUnrefreshedProbe: Title (no OnRefreshAsync override on VerifyUnrefreshedProbeActions)");
        reported.Should().Contain(HashUnchanged);
    }

    [Fact]
    public void A_refresh_trigger_whose_actions_class_inherits_OnRefreshAsync_exits_3()
    {
        // The actions class exists, but the only OnRefreshAsync it has is the base no-op.
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, nameof(VerifyInheritedRefreshProbe), [Attribute("Title", triggersRefresh: "Auto")]));

        exitCode.Should().Be(3);
        reported.Should().Contain("no OnRefreshAsync override on VerifyInheritedRefreshProbeActions");
    }

    [Fact]
    public void A_refresh_trigger_with_an_OnRefreshAsync_override_verifies()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, nameof(VerifyRefreshedProbe), [Attribute("Title", triggersRefresh: "Auto")]));

        exitCode.Should().Be(0, reported);
    }

    [Fact]
    public void A_None_refresh_trigger_is_no_trigger_and_needs_no_OnRefreshAsync()
    {
        // `None` is the same statement as leaving the field out, so a type with no actions class at
        // all must verify. A check that asked `is not null` instead of `!= None` would exit 3 here.
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifyNoneTriggerProbe", [Attribute("Title", triggersRefresh: "None")]));

        exitCode.Should().Be(0, reported);
        reported.Should().NotContain("declares refresh triggers that nothing implements");
    }

    [Fact]
    public void Blur_on_a_discrete_editor_warns_that_it_behaves_as_ValueChanged_but_verifies()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, nameof(VerifyRefreshedProbe),
            [
                Attribute("Mode", dataType: "boolean", triggersRefresh: "Blur"),
                // A lookup carries its key's data type; the lookup type is what makes it a select.
                Attribute("Status", lookupReferenceType: "CarStatus", triggersRefresh: "Blur"),
                // Free text: Blur means what it says, so no warning.
                Attribute("Title", triggersRefresh: "Blur"),
                // Discrete, but not Blur: nothing to warn about.
                Attribute("When", dataType: "date", triggersRefresh: "ValueChanged"),
            ]));

        exitCode.Should().Be(0, reported);
        reported.Should().Contain("\"Blur\" on a discrete editor behaves as \"ValueChanged\"");
        reported.Should().Contain("VerifyRefreshedProbe.Mode");
        reported.Should().Contain("VerifyRefreshedProbe.Status");
        reported.Should().NotContain("VerifyRefreshedProbe.Title");
        reported.Should().NotContain("VerifyRefreshedProbe.When");
    }

    // --- collection columns ------------------------------------------------

    [Fact]
    public void CanSort_on_a_collection_attribute_and_on_its_column_override_exits_3()
    {
        using var scratch = new ScratchContentRoot();
        var query = Query("GetVerifyCollectionProbes", "Database.VerifyCollectionProbes");
        query["columns"] = new JsonArray(new JsonObject { ["name"] = "Tags", ["canSort"] = true });

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifyCollectionProbe",
                [Attribute("Title"), Attribute("Tags", isArray: true, canSort: true)],
                [query]));

        exitCode.Should().Be(3);
        reported.Should().Contain("claims a collection column can be sorted");
        reported.Should().Contain("VerifyCollectionProbe.Tags: canSort on a collection");
        reported.Should().Contain("VerifyCollectionProbe.GetVerifyCollectionProbes.Tags: canSort override on a collection");
    }

    // --- sort columns and column overrides --------------------------------

    [Theory]
    [InlineData("Missing", "sorts by 'Missing', which is not an attribute of this type")]
    [InlineData("Secret", "sorts by 'Secret', which is showedOn 'PersistentObject' and so is not on the query surface")]
    public void A_sort_column_off_the_query_surface_exits_3(string property, string expected)
    {
        using var scratch = new ScratchContentRoot();
        var query = Query("GetVerifySortProbes", "Database.VerifySortProbes");
        query["sortColumns"] = new JsonArray(new JsonObject { ["property"] = property, ["direction"] = "asc" });

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifySortProbe", [Attribute("Title"), Attribute("Secret", showedOn: "PersistentObject")], [query]));

        exitCode.Should().Be(3);
        reported.Should().Contain("queries whose declared sort column is not sortable");
        reported.Should().Contain("VerifySortProbe.GetVerifySortProbes: " + expected);
    }

    /// <summary>
    /// The execute endpoint answers 400 for a bad direction, but a declared sort never passes through
    /// it, so a typo in the model would still sort ascending in silence (PRD D5).
    /// </summary>
    [Theory]
    [InlineData("dsc")]
    [InlineData("descending")]
    public void A_declared_sort_direction_other_than_asc_or_desc_exits_3(string direction)
    {
        using var scratch = new ScratchContentRoot();
        var query = Query("GetVerifySortProbes", "Database.VerifySortProbes");
        query["sortColumns"] = new JsonArray(new JsonObject { ["property"] = "Title", ["direction"] = direction });

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifySortProbe", [Attribute("Title")], [query]));

        exitCode.Should().Be(3);
        reported.Should().Contain($"VerifySortProbe.GetVerifySortProbes: sorts by 'Title' in direction '{direction}', which is neither 'asc' nor 'desc'");
    }

    [Theory]
    [InlineData("Missing", "'Missing' is not an attribute")]
    [InlineData("Secret", "'Secret' is not on the query surface (showedOn)")]
    public void A_column_override_that_resolves_to_nothing_exits_3(string column, string expected)
    {
        using var scratch = new ScratchContentRoot();
        var query = Query("GetVerifyColumnProbes", "Database.VerifyColumnProbes");
        query["columns"] = new JsonArray(new JsonObject { ["name"] = column, ["canFilter"] = false });

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifyColumnProbe", [Attribute("Title"), Attribute("Secret", showedOn: "PersistentObject")], [query]));

        exitCode.Should().Be(3);
        reported.Should().Contain("query column overrides that resolve to nothing");
        reported.Should().Contain("VerifyColumnProbe.GetVerifyColumnProbes: " + expected);
    }

    // --- Custom.* sources ------------------------------------------------

    [Theory]
    [InlineData("VerifyNoActionsProbe", "Custom.Anything", "no class named 'VerifyNoActionsProbeActions' exists")]
    [InlineData(nameof(VerifyCustomProbe), "Custom.Missing", "'VerifyCustomProbeActions' has no method named 'Missing'")]
    public void A_custom_source_with_no_method_to_serve_it_exits_3(string typeName, string source, string expected)
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, typeName, [Attribute("Title")], [Query("GetRows", source)]));

        exitCode.Should().Be(3);
        reported.Should().Contain($"Query 'GetRows' has source '{source}', but ");
        reported.Should().Contain(expected);
    }

    [Fact]
    public void A_custom_source_whose_method_exists_verifies()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, nameof(VerifyCustomProbe), [Attribute("Title")],
                [Query("GetRows", "Custom." + nameof(VerifyCustomProbeActions.Recent))]));

        exitCode.Should().Be(0, reported);
    }

    // --- sub-queries -----------------------------------------------------

    [Fact]
    public void A_sub_query_with_a_Database_source_exits_3()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () =>
            Plant(scratch, "VerifyParentProbe", [Attribute("Title")],
                [Query("GetVerifyChildren", "Database.VerifyChildren", alias: "verify-children")],
                subQueries: ["verify-children", "resolves-to-nothing"]));

        exitCode.Should().Be(3);
        reported.Should().Contain("'VerifyParentProbe' lists 'verify-children' as a sub-query, but query " +
                                  "'GetVerifyChildren' has source 'Database.VerifyChildren'");
        reported.Should().NotContain("resolves-to-nothing", "an unresolvable alias is the pruner's warning, not this check's");
    }

    // --- sub-query parentReference (#460, D19) -----------------------------

    private const string ParentClrType = "Verify.ParentRefProbe";

    /// <summary>
    /// A parent that lists a Custom.* sub-query over <see cref="VerifyChildRefProbe"/>, whose query
    /// names <paramref name="parentReference"/>. The child has one single Reference to the parent.
    /// </summary>
    private static void PlantParentReference(ScratchContentRoot scratch, string parentReference)
    {
        Plant(scratch, "VerifyParentRefProbe", [Attribute("Title")], subQueries: ["verify-ref-children"]);
        var parentPath = Path.Combine(scratch.ModelPath, "VerifyParentRefProbe.json");
        var parent = JsonNode.Parse(File.ReadAllText(parentPath))!.AsObject();
        parent["persistentObject"]!["clrType"] = ParentClrType;
        File.WriteAllText(parentPath, parent.ToJsonString());

        var reference = Attribute("Parent");
        reference["dataType"] = "Reference";
        reference["referenceType"] = ParentClrType;
        var query = Query("GetVerifyRefChildren", "Custom." + nameof(VerifyChildRefProbeActions.Children), alias: "verify-ref-children");
        query["parentReference"] = parentReference;
        Plant(scratch, nameof(VerifyChildRefProbe), [Attribute("Title"), reference], [query]);
    }

    [Theory]
    [InlineData("Missing", "names parentReference 'Missing', which is not an attribute of 'VerifyChildRefProbe'.")]
    [InlineData("Title", "names parentReference 'Title', which is not a single Reference attribute of 'VerifyChildRefProbe'.")]
    public void An_invalid_parentReference_exits_3(string parentReference, string expected)
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () => PlantParentReference(scratch, parentReference));

        exitCode.Should().Be(3);
        reported.Should().Contain("query 'GetVerifyRefChildren' (a sub-query of 'VerifyParentRefProbe') " + expected);
        reported.Should().Contain(HashUnchanged);
    }

    [Fact]
    public void A_parentReference_naming_the_single_Reference_to_the_parent_verifies()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () => PlantParentReference(scratch, "Parent"));

        exitCode.Should().Be(0, reported);
    }

    // --- program units pointing at two different targets ------------------

    [Fact]
    public void A_query_unit_whose_alias_and_id_name_different_queries_exits_3()
    {
        using var scratch = new ScratchContentRoot();
        var query = TheSyncProbeQueryId(Synchronized(scratch));

        var (exitCode, reported) = VerifyWith(scratch, () =>
        {
            Plant(scratch, "VerifyOtherProbe", [Attribute("Title")],
                [Query("GetOthers", "Database.Others", alias: "other-rows")]);
            PlantUnits(scratch, $$"""
                { "id": "22222222-2222-2222-2222-222222222222", "name": { "en": "The unit" }, "type": "query",
                  "queryId": "{{query}}", "alias": "other-rows", "order": 1 }
                """);
        });

        exitCode.Should().Be(3);
        reported.Should().Contain("routes to alias 'other-rows', which resolves to query 'GetOthers'");
        reported.Should().Contain("would open the wrong query");
    }

    [Fact]
    public void A_persistent_object_unit_whose_alias_and_id_name_different_types_exits_3()
    {
        using var scratch = new ScratchContentRoot();
        var typeId = TheSyncProbeTypeId(Synchronized(scratch));

        var (exitCode, reported) = VerifyWith(scratch, () =>
        {
            Plant(scratch, "VerifyOtherPage", [Attribute("Title")]);
            PlantUnits(scratch, $$"""
                { "id": "55555555-5555-5555-5555-555555555555", "name": { "en": "The page" }, "type": "persistentObject",
                  "persistentObjectId": "{{typeId}}", "alias": "verifyotherpage", "objectId": "main", "order": 1 }
                """);
        });

        exitCode.Should().Be(3);
        reported.Should().Contain("routes to alias 'verifyotherpage', which resolves to 'VerifyOtherPage'");
        reported.Should().Contain("would open the wrong page");
    }

    [Fact]
    public void A_query_unit_whose_alias_resolves_to_nothing_and_whose_id_is_unknown_says_so()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = VerifyWith(scratch, () => PlantUnits(scratch, """
            { "id": "22222222-2222-2222-2222-222222222222", "name": { "en": "Orphan" }, "type": "query",
              "queryId": "99999999-9999-9999-9999-999999999999", "alias": "nowhere", "order": 1 }
            """));

        exitCode.Should().Be(3);
        reported.Should().Contain("Program unit 'Orphan' routes to alias 'nowhere', but that alias resolves to no query.");
        reported.Should().Contain("give the unit an alias that resolves",
            "with no target on the id side either, the only fix to name is the unit's own alias");
    }

    private static void PlantUnits(ScratchContentRoot scratch, string unit) =>
        File.WriteAllText(Path.Combine(scratch.Path, "App_Data", "programUnits.json"), $$"""
            { "programUnitGroups": [ { "id": "11111111-1111-1111-1111-111111111111", "name": { "en": "Group" },
              "order": 1, "programUnits": [ {{unit}} ] } ] }
            """);

    private static JsonObject SyncProbeFile(string root) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(root, "App_Data", "Model", "SyncProbe.json")))!.AsObject();

    private static Guid TheSyncProbeQueryId(string root) =>
        Guid.Parse(SyncProbeFile(root)["queries"]![0]!["id"]!.GetValue<string>());

    private static Guid TheSyncProbeTypeId(string root) =>
        Guid.Parse(SyncProbeFile(root)["persistentObject"]!["id"]!.GetValue<string>());

    // --- composed queries --------------------------------------------------

    [Fact]
    public void A_composed_query_that_streams_or_shows_no_columns_exits_3()
    {
        using var scratch = new ScratchContentRoot();
        var streaming = Query("StreamVerifyComposed", "Custom." + nameof(VerifyComposedProbeActions.Rows));
        streaming["isStreamingQuery"] = true;

        var (exitCode, reported) = VerifyWith(scratch, () =>
        {
            Plant(scratch, nameof(VerifyComposedProbe), [Attribute("Title")], [streaming]);
            Plant(scratch, "VerifyColumnlessProbe", [Attribute("Secret", showedOn: "PersistentObject")],
                [Query("GetVerifyColumnless", "Database.VerifyColumnless")]);
        });

        exitCode.Should().Be(3);
        reported.Should().Contain("Query 'StreamVerifyComposed' streams over 'VerifyComposedProbe', which declares no clrType");
        reported.Should().Contain("Query 'GetVerifyColumnless' returns rows of 'VerifyColumnlessProbe'");
    }

    // --- files the checks must step over ----------------------------------

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{ \"persistentObject\": null, \"queries\": [] }")]
    public void A_model_file_no_check_can_read_is_left_to_the_hash_comparison(string content)
    {
        // Every check skips such a file rather than throwing, so the run still finishes and still
        // reports the drift the unexpected file causes.
        using var scratch = new ScratchContentRoot();
        Synchronized(scratch);
        File.WriteAllText(Path.Combine(scratch.ModelPath, "Broken.json"), content);
        PlantUnits(scratch, """
            { "id": "22222222-2222-2222-2222-222222222222", "name": { "en": "Unit" }, "type": "query",
              "queryId": "99999999-9999-9999-9999-999999999999", "order": 1 }
            """);

        var (exitCode, reported) = SparkExtensionsTests.Verify(scratch);

        exitCode.Should().Be(3);
        reported.Should().Contain("Spark model is out of sync.");
        reported.Should().NotContain("Exception");
    }

    [Fact]
    public void Verify_with_no_model_directory_reports_the_missing_hash_file()
    {
        // Nothing was ever synchronized: every check returns early on the missing directory, and
        // the one thing left to say is that there is nothing to verify against.
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = SparkExtensionsTests.Verify(scratch);

        exitCode.Should().Be(3);
        reported.Should().Contain("Spark model is unverifiable");
        Directory.Exists(scratch.ModelPath).Should().BeFalse("verify writes nothing");
    }

    // --- attribute descriptions --------------------------------------------

    /// <summary>
    /// Descriptions live in <c>translations.json</c> (#467, D1/D5), which the structural hash does not
    /// cover. A seeded English description that is gone again is drift: synchronize would write it.
    /// </summary>
    [Fact]
    public void A_missing_description_is_drift_even_though_the_hash_matches()
    {
        using var scratch = new ScratchContentRoot();
        scratch.Synchronize<DescribedTestSparkContext>();

        var translations = Path.Combine(Path.GetDirectoryName(scratch.ModelPath)!, "translations.json");
        File.Exists(translations).Should().BeTrue("synchronize seeds the C# summary as the English description");
        File.Delete(translations);

        var (exitCode, reported) = scratch.Verify<DescribedTestSparkContext>();

        exitCode.Should().Be(3);
        reported.Should().Contain("Spark attribute descriptions are out of date:");
        reported.Should().Contain("model.DescribedProbe.attributes.Name.description: no layer of translations.json defines 'en', C# says \"What the probe is called.\"");
    }

    // --- the generic overload and misconfiguration --------------------------

    [Fact]
    public void The_generic_overload_verifies_too()
    {
        using var scratch = new ScratchContentRoot();
        Synchronized(scratch);

        var (exitCode, reported) = scratch.Verify(
            builder => builder.SynchronizeSparkModelsIfRequested<OneEntityTestSparkContext>(["--spark-verify-model"]),
            args: ["--not-a-spark-command"]);

        exitCode.Should().Be(0, reported);
    }

    [Fact]
    public void A_factory_registered_context_is_reported_as_misconfigured()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = scratch.Verify(builder =>
            builder.Services.AddScoped<SparkContext>(_ => new OneEntityTestSparkContext()));

        exitCode.Should().Be(2);
        reported.Should().Contain("the registered SparkContext has no implementation type");
    }

    [Fact]
    public void An_abstract_registered_context_is_refused_by_verify()
    {
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = scratch.Verify(builder =>
            builder.Services.AddScoped<SparkContext, AbstractVerifyContext>());

        exitCode.Should().Be(2);
        reported.Should().Contain("'AbstractVerifyContext' is not a concrete SparkContext");
    }

    [Fact]
    public void A_registration_whose_implementation_is_not_a_SparkContext_is_refused()
    {
        // Unreachable through the generic overload's constraint; only a hand-built descriptor gets here.
        using var scratch = new ScratchContentRoot();

        var (exitCode, reported) = scratch.Verify(builder =>
            builder.Services.Add(new ServiceDescriptor(typeof(SparkContext), typeof(string), ServiceLifetime.Scoped)));

        exitCode.Should().Be(2);
        reported.Should().Contain("'String' does not derive from SparkContext");
    }

    // --- fixtures ------------------------------------------------------------

    public sealed class VerifyInheritedRefreshProbe { public string? Id { get; set; } }

    public sealed class VerifyInheritedRefreshProbeActions(IEntityMapper mapper)
        : DefaultPersistentObjectActions<VerifyInheritedRefreshProbe>(mapper);

    public sealed class VerifyRefreshedProbe { public string? Id { get; set; } }

    public sealed class VerifyRefreshedProbeActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VerifyRefreshedProbe>(mapper)
    {
        public override Task OnRefreshAsync(SparkRefreshArgs<VerifyRefreshedProbe> args) => Task.CompletedTask;
    }

    public sealed class VerifyCustomProbe { public string? Id { get; set; } }

    public sealed class VerifyCustomProbeActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VerifyCustomProbe>(mapper)
    {
        public IEnumerable<VerifyCustomProbe> Recent() => [];
    }

    public sealed class VerifyChildRefProbe { public string? Id { get; set; } }

    public sealed class VerifyChildRefProbeActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VerifyChildRefProbe>(mapper)
    {
        public IEnumerable<VerifyChildRefProbe> Children() => [];
    }

    public sealed class VerifyComposedProbe { public string? Id { get; set; } }

    public sealed class VerifyComposedProbeActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VerifyComposedProbe>(mapper)
    {
        public IEnumerable<VerifyComposedProbe> Rows() => [];
    }

    public sealed class DescribedProbe
    {
        public string? Id { get; set; }

        [Description("What the probe is called.")]
        public string? Name { get; set; }
    }

    public sealed class DescribedTestSparkContext : SparkContext
    {
        public IRavenQueryable<DescribedProbe> DescribedProbes => Session.Query<DescribedProbe>();
    }

    public abstract class AbstractVerifyContext : SparkContext;
}
