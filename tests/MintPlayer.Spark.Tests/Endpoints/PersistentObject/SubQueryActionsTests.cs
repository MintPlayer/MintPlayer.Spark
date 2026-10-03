using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// #460 M15 — sub-query selection &amp; actions, through the real route table: the default New and
/// Delete as catalogue entries (D18), the bulk delete's rule, cap and all-or-nothing guarantee, and
/// the sub-query New's <c>OnNewAsync</c> with the parent-reference auto-fill (D19).
/// </summary>
/// <remarks>
/// Fixture names start with <c>Bq</c>: <c>ActionsResolver</c> matches actions classes by simple name
/// across the whole assembly, and these types must not meet another fixture's.
/// </remarks>
public class SubQueryActionsTests : SparkTestDriver
{
    private static readonly Guid ParentTypeId = Guid.Parse("46150000-0000-4000-8000-000000000001");
    private static readonly Guid ChildTypeId = Guid.Parse("46150000-0000-4000-8000-000000000002");
    private static readonly Guid TwoRefTypeId = Guid.Parse("46150000-0000-4000-8000-000000000003");
    private static readonly Guid NoRefTypeId = Guid.Parse("46150000-0000-4000-8000-000000000004");
    private static readonly Guid NoBaseTypeId = Guid.Parse("46150000-0000-4000-8000-000000000005");
    private static readonly Guid HelperTypeId = Guid.Parse("46150000-0000-4000-8000-000000000006");
    private static readonly Guid SoftTypeId = Guid.Parse("46150000-0000-4000-8000-000000000007");
    private static readonly Guid ChildrenQueryId = Guid.Parse("46150000-0000-4000-8000-000000000011");

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<Host> StartAsync(SparkTestSecurity? security = null)
    {
        var factory = new SparkEndpointFactory<BqContext>(
            Store,
            Models(),
            configureServices: s => s.AddSingleton<BqRecorder>(),
            configureSpark: spark => spark.AddSoftDelete(),
            security: security ?? SparkTestSecurity.Permissive);
        factories.Add(factory);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new Host(factory, factory.CreateClient(), cookie, xsrf);
    }

    private Task SeedChildrenAsync(params string[] titles) => SeedAsync(async session =>
    {
        await session.StoreAsync(new BqParent { Id = "BqParents/1", Name = "parent" });
        foreach (var title in titles)
            await session.StoreAsync(new BqChild { Id = $"BqChildren/{title}", Title = title, ParentId = "BqParents/1" });
    });

    private async Task<bool> ExistsAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id) is not null;
    }

    private static object DeleteMany(params string[] ids)
        => Wire.Typed(ChildTypeId, new { ids, queryId = ChildrenQueryId.ToString(), parentId = "BqParents/1", parentType = "BqParent" });

    // ---- D18: default actions in the catalogue ---------------------------------------------------

    [Fact]
    public async Task The_list_offers_New_Edit_and_Delete_as_default_entries_with_their_rules()
    {
        var host = await StartAsync();

        var (status, body) = await host.SendAsync("/spark/actions/list", Wire.Typed(ChildTypeId));

        status.Should().Be(HttpStatusCode.OK);
        var actions = body.EnumerateArray().ToList();
        var newAction = actions.Single(a => a.GetProperty("name").GetString() == "New");
        var editAction = actions.Single(a => a.GetProperty("name").GetString() == "Edit");
        var deleteAction = actions.Single(a => a.GetProperty("name").GetString() == "Delete");
        newAction.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        newAction.TryGetProperty("selectionRule", out var newRule).Should().BeTrue();
        newRule.ValueKind.Should().Be(JsonValueKind.Null, "New acts on the query, not on rows");
        newAction.GetProperty("showedOn").GetString().Should().Be("query");
        editAction.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        editAction.GetProperty("selectionRule").GetString().Should().Be("=1");
        editAction.GetProperty("showedOn").GetString().Should().Be("both");
        deleteAction.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        deleteAction.GetProperty("selectionRule").GetString().Should().Be(">0");
        deleteAction.GetProperty("showedOn").GetString().Should().Be("both");
    }

    [Fact]
    public async Task A_default_entry_is_listed_only_for_a_caller_holding_its_right()
    {
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("Delete/BqChild"));

        var (_, body) = await host.SendAsync("/spark/actions/list", Wire.Typed(ChildTypeId));

        var names = body.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        names.Should().Contain("New");
        names.Should().NotContain("Delete");
    }

    [Theory]
    [InlineData("New")]
    [InlineData("Edit")]
    [InlineData("Delete")]
    public async Task Execute_refuses_a_default_action_name(string name)
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a");

        var (status, _) = await host.SendAsync("/spark/actions/execute",
            Wire.Action(ChildTypeId, name, new { selectedItemIds = new[] { "BqChildren/a" } }));

        status.Should().Be(HttpStatusCode.NotFound, "New, Edit and Delete run through their own endpoints only");
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeTrue();
    }

    // ---- D18: bulk delete ------------------------------------------------------------------------

    [Fact]
    public async Task A_bulk_delete_removes_every_selected_row_in_one_request()
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a", "b", "c");

        var (status, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a", "BqChildren/b"));

        status.Should().Be(HttpStatusCode.NoContent);
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeFalse();
        (await ExistsAsync<BqChild>("BqChildren/b")).Should().BeFalse();
        (await ExistsAsync<BqChild>("BqChildren/c")).Should().BeTrue("only the selection is deleted");
    }

    [Fact]
    public async Task The_Delete_rule_refuses_an_empty_selection()
    {
        var host = await StartAsync();
        var (status, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany());
        status.Should().Be(HttpStatusCode.BadRequest, "Delete is '>0'");
    }

    [Fact]
    public async Task An_application_override_of_the_Delete_rule_is_enforced()
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a", "b");
        var root = host.Factory.GetService<IHostEnvironment>().ContentRootPath;
        await File.WriteAllTextAsync(Path.Combine(root, "App_Data", "actions.json"),
            """{ "Delete": { "selectionRule": "=1" } }""");
        // The catalogue is composed at startup; the watcher would pick the file up a moment later.
        host.Factory.GetService<IActionsCatalogueLoader>().InvalidateCache();

        var (two, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a", "BqChildren/b"));
        two.Should().Be(HttpStatusCode.BadRequest, "the override narrowed Delete to exactly one row");
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeTrue();

        var (one, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a"));
        one.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task More_than_200_rows_are_refused_before_any_database_work()
    {
        var host = await StartAsync();
        var ids = Enumerable.Range(0, SparkDefaultActions.MaxSelectedItems + 1).Select(i => $"BqChildren/{i}").ToArray();

        var (status, body) = await host.SendAsync("/spark/po/delete-many", DeleteMany(ids));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.ToString().Should().Contain("200");
    }

    [Fact]
    public async Task A_missing_row_refuses_the_lot_like_a_missing_row()
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a");

        var (status, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a", "BqChildren/missing"));

        status.Should().Be(HttpStatusCode.NotFound, "never a silently shorter delete");
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeTrue();
    }

    [Fact]
    public async Task A_row_that_fails_mid_batch_leaves_every_row_in_place()
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a", "boom", "c");

        // "a" is queued for deletion before "boom"'s OnBeforeDeleteAsync refuses: one SaveChanges
        // means "a" was never written.
        var (status, _) = await host.SendAsync("/spark/po/delete-many",
            DeleteMany("BqChildren/a", "BqChildren/boom", "BqChildren/c"));

        status.Should().Be(HttpStatusCode.BadRequest);
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeTrue("all or nothing");
        (await ExistsAsync<BqChild>("BqChildren/boom")).Should().BeTrue();
        (await ExistsAsync<BqChild>("BqChildren/c")).Should().BeTrue();
    }

    [Fact]
    public async Task OnDisableActionsAsync_withholding_Delete_on_one_row_refuses_the_lot_with_403()
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a", "frozen");

        var (status, body) = await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a", "BqChildren/frozen"));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.ToString().Should().Contain("Delete");
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeTrue();
        (await ExistsAsync<BqChild>("BqChildren/frozen")).Should().BeTrue();
    }

    [Fact]
    public async Task OnDisableActionsAsync_is_asked_about_the_query_with_its_parent()
    {
        var host = await StartAsync();
        await SeedChildrenAsync("a");

        await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a"));

        host.Recorder.QueryTargets.Should().Equal("bq-children|BqParents/1|BqParent");
    }

    [Fact]
    public async Task A_row_denied_by_the_Delete_right_refuses_the_request()
    {
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("Delete/BqChild"));
        await SeedChildrenAsync("a");

        var (status, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany("BqChildren/a"));

        status.Should().NotBe(HttpStatusCode.NoContent);
        (await ExistsAsync<BqChild>("BqChildren/a")).Should().BeTrue();
    }

    [Fact]
    public async Task A_bulk_delete_of_a_soft_deletable_type_is_a_soft_delete()
    {
        var host = await StartAsync();
        await SeedAsync(async session =>
        {
            await session.StoreAsync(new BqSoft { Id = "BqSofts/1", Title = "one" });
            await session.StoreAsync(new BqSoft { Id = "BqSofts/2", Title = "two" });
        });

        var (status, _) = await host.SendAsync("/spark/po/delete-many", Wire.Typed(SoftTypeId, new { ids = new[] { "BqSofts/1", "BqSofts/2" } }));

        status.Should().Be(HttpStatusCode.NoContent);
        using var session = Store.OpenAsyncSession();
        var rows = await session.LoadAsync<BqSoft>(["BqSofts/1", "BqSofts/2"]);
        rows.Values.Should().AllSatisfy(row =>
        {
            row.Should().NotBeNull("a soft delete keeps the document");
            row!.IsDeleted.Should().BeTrue();
            row.DeletedAt.HasValue.Should().BeTrue();
        });
    }

    // ---- D19: New from a sub-query ---------------------------------------------------------------

    private async Task<Dictionary<string, JsonElement>> NewAsync(Host host, Guid typeId, string queryId)
    {
        var (status, body) = await host.SendAsync("/spark/po/new",
            Wire.Typed(typeId, new { parentId = "BqParents/1", parentType = "BqParent", queryId }));
        status.Should().Be(HttpStatusCode.OK);
        return body.GetProperty("result").GetProperty("attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.TryGetProperty("value", out var v) ? v : default);
    }

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Fact]
    public async Task OnNewAsync_receives_the_parent_its_type_and_the_sub_query()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        await NewAsync(host, ChildTypeId, "bq-children");

        host.Recorder.NewCalls.Should().Equal("BqParents/1|BqParent|bq-children");
    }

    [Fact]
    public async Task The_base_fills_the_single_reference_to_the_parent()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var attributes = await NewAsync(host, ChildTypeId, "bq-children");

        Text(attributes["ParentId"]).Should().Be("BqParents/1");
    }

    [Fact]
    public async Task A_type_with_no_reference_to_the_parent_is_left_alone()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var attributes = await NewAsync(host, NoRefTypeId, "bq-norefs");

        attributes.Values.Select(Text).Should().NotContain("BqParents/1");
    }

    [Fact]
    public async Task Two_references_to_the_parent_fill_nothing_without_an_explicit_name()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var attributes = await NewAsync(host, TwoRefTypeId, "bq-tworefs");

        Text(attributes["FirstParentId"]).Should().BeNull("guessing between two references is how a row lands under the wrong parent");
        Text(attributes["SecondParentId"]).Should().BeNull();
    }

    [Fact]
    public async Task An_explicit_parentReference_wins()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var attributes = await NewAsync(host, TwoRefTypeId, "bq-tworefs-named");

        Text(attributes["SecondParentId"]).Should().Be("BqParents/1");
        Text(attributes["FirstParentId"]).Should().BeNull();
    }

    [Fact]
    public async Task An_override_that_does_not_call_base_fills_nothing()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var attributes = await NewAsync(host, NoBaseTypeId, "bq-nobase");

        Text(attributes["ParentId"]).Should().BeNull("the hook owns initialisation once it skips base (D1)");
    }

    [Fact]
    public async Task An_override_that_calls_the_helper_fills_the_reference()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var attributes = await NewAsync(host, HelperTypeId, "bq-helper");

        Text(attributes["ParentId"]).Should().Be("BqParents/1");
    }

    [Fact]
    public async Task A_standalone_New_fills_nothing()
    {
        var host = await StartAsync();

        var (status, body) = await host.SendAsync("/spark/po/new", Wire.Typed(ChildTypeId));

        status.Should().Be(HttpStatusCode.OK);
        var parent = body.GetProperty("result").GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "ParentId");
        Text(parent.TryGetProperty("value", out var v) ? v : default).Should().BeNull();
    }

    [Fact]
    public async Task A_sub_query_the_parent_does_not_declare_is_refused()
    {
        var host = await StartAsync();
        await SeedChildrenAsync();

        var (status, _) = await host.SendAsync("/spark/po/new",
            Wire.Typed(ChildTypeId, new { parentId = "BqParents/1", parentType = "BqParent", queryId = "bq-softs" }));

        status.Should().Be(HttpStatusCode.NotFound, "the query must be the parent's sub-query and list the constructed type");
    }

    [Fact]
    public async Task A_parent_that_does_not_exist_is_refused()
    {
        var host = await StartAsync();

        var (status, _) = await host.SendAsync("/spark/po/new",
            Wire.Typed(ChildTypeId, new { parentId = "BqParents/missing", parentType = "BqParent", queryId = "bq-children" }));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- model -----------------------------------------------------------------------------------

    private static EntityAttributeDefinition Ref(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = typeof(BqParent).FullName,
    };

    private static EntityAttributeDefinition Str(string name) => new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };

    private static SparkQuery Query(string alias, string entityType, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), Name = alias, Alias = alias, Source = "Custom.None", EntityType = entityType,
    };

    private static EntityTypeFile Model<T>(Guid id, EntityAttributeDefinition[] attributes, params SparkQuery[] queries) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id, Name = typeof(T).Name, ClrType = typeof(T).FullName!, Attributes = attributes,
        },
        Queries = queries,
    };

    private static IEnumerable<EntityTypeFile> Models()
    {
        var parent = Model<BqParent>(ParentTypeId, [Str("Name")]);
        parent.PersistentObject.Queries =
        [
            "bq-children",
            "bq-norefs",
            "bq-tworefs",
            new SparkSubQuery { Query = "bq-tworefs-named", ParentReference = "SecondParentId" },
            "bq-nobase",
            "bq-helper",
        ];

        return
        [
            parent,
            Model<BqChild>(ChildTypeId, [Str("Title"), Ref("ParentId")], Query("bq-children", "BqChild", ChildrenQueryId)),
            Model<BqNoRef>(NoRefTypeId, [Str("Title")], Query("bq-norefs", "BqNoRef")),
            Model<BqTwoRef>(TwoRefTypeId, [Ref("FirstParentId"), Ref("SecondParentId")],
                Query("bq-tworefs", "BqTwoRef"), Query("bq-tworefs-named", "BqTwoRef")),
            Model<BqNoBase>(NoBaseTypeId, [Ref("ParentId")], Query("bq-nobase", "BqNoBase")),
            Model<BqHelper>(HelperTypeId, [Ref("ParentId")], Query("bq-helper", "BqHelper")),
            Model<BqSoft>(SoftTypeId, [Str("Title")], Query("bq-softs", "BqSoft")),
        ];
    }

    private sealed record Host(SparkEndpointFactory<BqContext> Factory, HttpClient Client, string Cookie, string Xsrf)
    {
        public BqRecorder Recorder => Factory.GetService<BqRecorder>();

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Headers.Add("Cookie", Cookie);
            request.Headers.Add("X-XSRF-TOKEN", Xsrf);
            var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
    }
}

public sealed class BqRecorder
{
    public List<string> NewCalls { get; } = [];
    public List<string> QueryTargets { get; } = [];
}

public class BqParent
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class BqChild
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    [Reference(typeof(BqParent))] public string? ParentId { get; set; }
}

public class BqNoRef
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class BqTwoRef
{
    public string? Id { get; set; }
    [Reference(typeof(BqParent))] public string? FirstParentId { get; set; }
    [Reference(typeof(BqParent))] public string? SecondParentId { get; set; }
}

public class BqNoBase
{
    public string? Id { get; set; }
    [Reference(typeof(BqParent))] public string? ParentId { get; set; }
}

public class BqHelper
{
    public string? Id { get; set; }
    [Reference(typeof(BqParent))] public string? ParentId { get; set; }
}

public class BqSoft : ISoftDeletable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>Refuses "boom" mid-batch, withholds Delete on "frozen", records the New and the query target.</summary>
public class BqChildActions(IEntityMapper entityMapper, BqRecorder recorder) : DefaultPersistentObjectActions<BqChild>(entityMapper),
    MintPlayer.Spark.Abstractions.Interceptors.IBeforeDelete<BqChild>
{
    public ValueTask OnBeforeDeleteAsync(BqChild entity, MintPlayer.Spark.Abstractions.Interceptors.DeleteContext context)
        => entity.Title == "boom"
            ? throw new SparkValidationException("boom refuses")
            : ValueTask.CompletedTask;

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.TargetKind == DisableActionsTargetKind.Query && context.Phase == DisableActionsPhase.Submit)
            recorder.QueryTargets.Add($"{context.Query?.Alias}|{context.Parent?.Id}|{context.ParentType}");

        if (context.Entity is BqChild { Title: "frozen" })
            target.DisableActions("Delete");
        return Task.CompletedTask;
    }

    public override async Task OnNewAsync(SparkNewArgs<BqChild> args)
    {
        recorder.NewCalls.Add($"{args.Parent?.Id}|{args.ParentType?.Name}|{args.Query?.Alias}");
        await base.OnNewAsync(args);
    }
}

/// <summary>Overrides OnNewAsync without calling base: the auto-fill is lost, as intended.</summary>
public class BqNoBaseActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<BqNoBase>(entityMapper)
{
    public override Task OnNewAsync(SparkNewArgs<BqNoBase> args) => Task.CompletedTask;
}

/// <summary>Overrides OnNewAsync and asks for the auto-fill through the helper instead of base.</summary>
public class BqHelperActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<BqHelper>(entityMapper)
{
    public override Task OnNewAsync(SparkNewArgs<BqHelper> args)
    {
        args.FillParentReference();
        return Task.CompletedTask;
    }
}

public class BqContext : SparkContext
{
    public IRavenQueryable<BqParent> Parents => Session.Query<BqParent>();
    public IRavenQueryable<BqChild> Children => Session.Query<BqChild>();
    public IRavenQueryable<BqNoRef> NoRefs => Session.Query<BqNoRef>();
    public IRavenQueryable<BqTwoRef> TwoRefs => Session.Query<BqTwoRef>();
    public IRavenQueryable<BqNoBase> NoBases => Session.Query<BqNoBase>();
    public IRavenQueryable<BqHelper> Helpers => Session.Query<BqHelper>();
    public IRavenQueryable<BqSoft> Softs => Session.Query<BqSoft>();
}
