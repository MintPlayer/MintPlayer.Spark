using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject.Selection;

// Fixture names start with I467: ActionsResolver matches actions classes by simple name across the
// whole assembly, and these must not meet another fixture's.

/// <summary>S4: a row whose Read rule (owner = alice) is narrower than its Delete rule (none).</summary>
public class I467Note
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
}

/// <summary>
/// Read/Query see only alice's notes; Delete has no row rule at all (wider than Read — gap G2).
/// A note titled "frozen" has Delete withheld by the object-level <c>OnDisableActionsAsync</c>,
/// which is what can turn a delete-many into a 403-vs-404 existence oracle.
/// </summary>
public class I467NoteActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<I467Note>(entityMapper)
{
    public override Task<Expression<Func<I467Note, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<I467Note, bool>>?>(
            string.Equals(action, "Delete", StringComparison.OrdinalIgnoreCase) ? null : n => n.Owner == "alice");

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is I467Note { Title: "frozen" })
            target.DisableActions("Delete");
        return Task.CompletedTask;
    }
}

/// <summary>S7: one type, two queries; the "locked" query disables Delete at query level.</summary>
public class I467Task
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool Locked { get; set; }
}

public class I467TaskActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<I467Task>(entityMapper)
{
    public const string LockedQueryAlias = "i467-locked-tasks";

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.TargetKind == DisableActionsTargetKind.Query
            && string.Equals(context.Query?.Alias, LockedQueryAlias, StringComparison.OrdinalIgnoreCase))
            target.DisableActions("Delete");
        return Task.CompletedTask;
    }
}

/// <summary>S4 (custom-action half): a custom action on notes, which must never see bob's unreadable note.</summary>
public sealed class I467TouchAction(I467TouchLog log) : MintPlayer.Spark.Abstractions.Actions.ICustomAction
{
    public const string Name = "I467Touch";

    public Task ExecuteAsync(MintPlayer.Spark.Abstractions.Actions.CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        log.Touched.AddRange(args.SelectedItems.Select(i => i.Id));
        return Task.CompletedTask;
    }
}

/// <summary>What <see cref="I467TouchAction"/> was handed.</summary>
public sealed class I467TouchLog
{
    public List<string> Touched { get; } = [];
}

internal sealed class I467ActionResolver(I467TouchAction touch) : ICustomActionResolver
{
    public MintPlayer.Spark.Abstractions.Actions.ICustomAction? Resolve(string name)
        => string.Equals(name, I467TouchAction.Name, StringComparison.OrdinalIgnoreCase) ? touch : null;

    public IReadOnlyList<string> GetRegisteredActionNames() => [I467TouchAction.Name];
}

/// <summary>S8 (retry half): a soft-free row whose before-delete interceptor asks once, inside a bulk delete.</summary>
public class I467Prompt
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class I467PromptActions(IEntityMapper entityMapper, MintPlayer.Spark.Abstractions.Retry.IRetryAccessor retry)
    : DefaultPersistentObjectActions<I467Prompt>(entityMapper), MintPlayer.Spark.Abstractions.Interceptors.IBeforeDelete<I467Prompt>
{
    public ValueTask OnBeforeDeleteAsync(I467Prompt entity, MintPlayer.Spark.Abstractions.Interceptors.DeleteContext context)
    {
        // Ask only until answered; once per request, however many rows the batch has.
        if (retry.Result is null)
            retry.Action("Delete these?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
        return ValueTask.CompletedTask;
    }
}

/// <summary>S5: a plain row, no actions class.</summary>
public class I467Item
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

public class I467Context : SparkContext
{
    public IRavenQueryable<I467Note> Notes => Session.Query<I467Note>();
    public IRavenQueryable<I467Task> OpenTasks => (IRavenQueryable<I467Task>)Session.Query<I467Task>().Where(t => !t.Locked);
    public IRavenQueryable<I467Task> LockedTasks => (IRavenQueryable<I467Task>)Session.Query<I467Task>().Where(t => t.Locked);
    public IRavenQueryable<I467Item> Items => Session.Query<I467Item>();
    public IRavenQueryable<I467Prompt> Prompts => Session.Query<I467Prompt>();
}

public static class I467Models
{
    public static readonly Guid NoteTypeId = Guid.Parse("46700000-0000-4000-8000-000000000201");
    public static readonly Guid TaskTypeId = Guid.Parse("46700000-0000-4000-8000-000000000202");
    public static readonly Guid ItemTypeId = Guid.Parse("46700000-0000-4000-8000-000000000203");
    public static readonly Guid NotesQueryId = Guid.Parse("46700000-0000-4000-8000-000000000211");
    public static readonly Guid OpenTasksQueryId = Guid.Parse("46700000-0000-4000-8000-000000000212");
    public static readonly Guid LockedTasksQueryId = Guid.Parse("46700000-0000-4000-8000-000000000213");
    public static readonly Guid ItemsQueryId = Guid.Parse("46700000-0000-4000-8000-000000000214");
    public static readonly Guid PromptTypeId = Guid.Parse("46700000-0000-4000-8000-000000000204");
    public static readonly Guid PromptsQueryId = Guid.Parse("46700000-0000-4000-8000-000000000215");

    private static EntityAttributeDefinition Str(string name) => new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };
    private static EntityAttributeDefinition Bool(string name) => new() { Id = Guid.NewGuid(), Name = name, DataType = "bool" };

    private static EntityTypeFile Model<T>(Guid id, string breadcrumb, EntityAttributeDefinition[] attributes, params SparkQuery[] queries) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id, Name = typeof(T).Name, ClrType = typeof(T).FullName!, Breadcrumb = breadcrumb, Attributes = attributes,
        },
        Queries = queries,
    };

    public static EntityTypeFile[] All() =>
    [
        Model<I467Note>(NoteTypeId, "{Title}", [Str("Title"), Str("Owner")],
            new SparkQuery { Id = NotesQueryId, Name = "i467-notes", Alias = "i467-notes", Source = "Database.Notes", EntityType = "I467Note" }),
        Model<I467Task>(TaskTypeId, "{Title}", [Str("Title"), Bool("Locked")],
            new SparkQuery { Id = OpenTasksQueryId, Name = "i467-open-tasks", Alias = "i467-open-tasks", Source = "Database.OpenTasks", EntityType = "I467Task" },
            new SparkQuery { Id = LockedTasksQueryId, Name = I467TaskActions.LockedQueryAlias, Alias = I467TaskActions.LockedQueryAlias, Source = "Database.LockedTasks", EntityType = "I467Task" }),
        Model<I467Item>(ItemTypeId, "{Name}", [Str("Name"), Str("Remark")],
            new SparkQuery { Id = ItemsQueryId, Name = "i467-items", Alias = "i467-items", Source = "Database.Items", EntityType = "I467Item" }),
        Model<I467Prompt>(PromptTypeId, "{Name}", [Str("Name")],
            new SparkQuery { Id = PromptsQueryId, Name = "i467-prompts", Alias = "i467-prompts", Source = "Database.Prompts", EntityType = "I467Prompt" }),
    ];
}

/// <summary>A started host plus an antiforgery pair, sending raw JSON so the body shape is explicit.</summary>
public sealed record I467Host(SparkEndpointFactory<I467Context> Factory, HttpClient Client, string Cookie, string Xsrf) : IAsyncDisposable
{
    public static async Task<I467Host> StartAsync(Raven.Client.Documents.IDocumentStore store)
    {
        var factory = new SparkEndpointFactory<I467Context>(store, I467Models.All(),
            configureServices: services =>
            {
                services.AddSingleton<I467TouchLog>();
                services.AddScoped<I467TouchAction>();
                services.AddSingleton(_Infrastructure.TestActions.LoaderWithCustom(I467TouchAction.Name));
                services.AddScoped<ICustomActionResolver, I467ActionResolver>();
            },
            security: SparkTestSecurity.Permissive);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new I467Host(factory, factory.CreateClient(), cookie, xsrf);
    }

    public async Task<(HttpStatusCode Status, string Body)> SendAsync(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", Cookie);
        request.Headers.Add("X-XSRF-TOKEN", Xsrf);
        var response = await Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The etag the detail page hands out (<c>POST /spark/po/load</c>).</summary>
    public async Task<string?> LoadEtagAsync(Guid typeId, string id)
    {
        var (status, body) = await SendAsync("/spark/po/load", Wire.Typed(typeId, id: id));
        if (status != HttpStatusCode.OK) return null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.TryGetProperty("result", out var result) ? result : doc.RootElement;
        return root.TryGetProperty("etag", out var etag) ? etag.GetString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}
