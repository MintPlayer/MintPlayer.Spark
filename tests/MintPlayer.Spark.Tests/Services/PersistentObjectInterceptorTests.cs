using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #460 item 1 — <see cref="IPersistentObjectInterceptor"/> in <c>DatabaseAccess</c>, including spike
/// S4: a delete replacement decided where an <c>OnDeleteAsync</c> override cannot defeat it, and
/// replication forwarding a save rather than a hard delete.
/// </summary>
public class PersistentObjectInterceptorTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46a1c7e0-4600-4600-4600-46a1c7e04600");

    private SparkEndpointFactory<InterceptedContext> factory = null!;
    private readonly InterceptionLog log = new();
    private readonly RecordingSyncInterceptor sync = new();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(NoteTypeId)],
            configureServices: services =>
            {
                services.AddSingleton(log);
                services.AddSingleton<ISyncActionInterceptor>(sync);
            },
            configureSpark: spark => spark
                .AddPersistentObjectInterceptor<ReplacingDeleteInterceptor>()
                .AddPersistentObjectInterceptor<StampingInterceptor>()
                .AddPersistentObjectInterceptor<StampingInterceptor>());   // twice: a no-op
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedAsync() => SeedAsync(async session =>
        await session.StoreAsync(new InterceptedNote { Id = "InterceptedNotes/1", Title = "first" }));

    [Fact]
    public async Task S4_a_replaced_delete_survives_an_OnDeleteAsync_override_and_replicates_as_a_save()
    {
        await SeedAsync();
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            await db.DeletePersistentObjectAsync(NoteTypeId, "InterceptedNotes/1");
        }

        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<InterceptedNote>("InterceptedNotes/1");
        stored.Should().NotBeNull("the interceptor replaced the delete, so the document survives");
        stored!.IsDeleted.Should().BeTrue("the replacement's changes to the tracked entity were saved");

        log.Entries.Should().NotContain("actions.OnDeleteAsync",
            "a replaced delete never reaches the Actions class's OnDeleteAsync — not even its override");
        log.Entries.Should().Equal(
            "actions.OnBeforeDeleteAsync",
            "replace.OnBeforeDeleteAsync",
            "stamp.OnBeforeDeleteAsync",
            "stamp.OnAfterDeleteAsync",
            "replace.OnAfterDeleteAsync");

        sync.Deletes.Should().BeEmpty("a soft delete must not reach the owner module as a hard delete");
        sync.Saves.Should().Equal("InterceptedNotes/1");
    }

    [Fact]
    public async Task A_purge_is_not_replaced_and_replicates_as_a_delete()
    {
        await SeedAsync();
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            await db.DeletePersistentObjectAsync(NoteTypeId, "InterceptedNotes/1", PersistentObjectOperation.Purge);
        }

        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<InterceptedNote>("InterceptedNotes/1")).Should().BeNull();

        log.Entries.Should().Equal(
            "actions.OnBeforeDeleteAsync",
            "replace.OnBeforeDeleteAsync",
            "stamp.OnBeforeDeleteAsync",
            "actions.OnDeleteAsync",
            "stamp.OnAfterDeleteAsync",
            "replace.OnAfterDeleteAsync");
        sync.Deletes.Should().Equal("InterceptedNotes/1");
        sync.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_hooks_run_after_the_actions_hook_in_order_and_after_hooks_in_reverse()
    {
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var po = new PersistentObject { ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "new", IsValueChanged = true });
            var saved = await db.SavePersistentObjectAsync(po);
            saved.Id.Should().NotBeNullOrEmpty();
        }

        log.Entries.Should().Equal(
            "actions.OnBeforeSaveAsync",
            "replace.OnBeforeSaveAsync:New",
            "stamp.OnBeforeSaveAsync:New",
            "stamp.OnAfterSaveAsync",
            "replace.OnAfterSaveAsync");

        using var verify = Store.OpenAsyncSession();
        var stored = await verify.Query<InterceptedNote>().Customize(c => c.WaitForNonStaleResults()).SingleAsync();
        stored.Stamp.Should().Be("stamped", "a before-save interceptor's mutation is what gets written");
    }

    [Fact]
    public async Task After_load_interceptors_can_decorate_the_loaded_object()
    {
        await SeedAsync();
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var po = await db.GetPersistentObjectAsync(NoteTypeId, "InterceptedNotes/1");

        po.Should().NotBeNull();
        po!.Breadcrumb.Should().Be("first (loaded)");
    }
}

public class InterceptedNote
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public string? Stamp { get; set; }
}

/// <summary>Overrides OnDeleteAsync WITHOUT calling the base — the case D1 says must not defeat a replacement.</summary>
public class InterceptedNoteActions : DefaultPersistentObjectActions<InterceptedNote>
{
    private readonly InterceptionLog log;

    public InterceptedNoteActions(IEntityMapper entityMapper, InterceptionLog log) : base(entityMapper) => this.log = log;

    public override Task OnBeforeSaveAsync(PersistentObject obj, InterceptedNote entity)
    {
        log.Entries.Add("actions.OnBeforeSaveAsync");
        return Task.CompletedTask;
    }

    public override Task OnBeforeDeleteAsync(InterceptedNote entity)
    {
        log.Entries.Add("actions.OnBeforeDeleteAsync");
        return Task.CompletedTask;
    }

    public override async Task OnDeleteAsync(IAsyncDocumentSession session, string id)
    {
        log.Entries.Add("actions.OnDeleteAsync");
        session.Delete(id);
        await session.SaveChangesAsync();
    }
}

public sealed class InterceptionLog
{
    public List<string> Entries { get; } = [];
}

public sealed class ReplacingDeleteInterceptor(InterceptionLog log) : IPersistentObjectInterceptor
{
    public bool AppliesTo(Type entityType) => entityType == typeof(InterceptedNote);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        log.Entries.Add($"replace.OnBeforeSaveAsync:{context.Operation}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterSaveAsync(SaveContext context)
    {
        log.Entries.Add("replace.OnAfterSaveAsync");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        log.Entries.Add("replace.OnBeforeDeleteAsync");
        if (!context.IsPurge)
        {
            ((InterceptedNote)context.Entity).IsDeleted = true;
            context.Replace();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        log.Entries.Add("replace.OnAfterDeleteAsync");
        return ValueTask.CompletedTask;
    }
}

public sealed class StampingInterceptor(InterceptionLog log) : IPersistentObjectInterceptor
{
    public bool AppliesTo(Type entityType) => entityType == typeof(InterceptedNote);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        log.Entries.Add($"stamp.OnBeforeSaveAsync:{context.Operation}");
        ((InterceptedNote)context.Entity!).Stamp = "stamped";
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterSaveAsync(SaveContext context)
    {
        log.Entries.Add("stamp.OnAfterSaveAsync");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        log.Entries.Add("stamp.OnBeforeDeleteAsync");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        log.Entries.Add("stamp.OnAfterDeleteAsync");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterLoadAsync(LoadContext context)
    {
        context.PersistentObject.Breadcrumb += " (loaded)";
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingSyncInterceptor : ISyncActionInterceptor
{
    public List<string> Saves { get; } = [];
    public List<string> Deletes { get; } = [];

    public bool IsReplicated(Type entityType) => entityType == typeof(InterceptedNote);

    public Task HandleSaveAsync(Type entityType, PersistentObject obj, bool isNew)
    {
        Saves.Add(obj.Id ?? "(new)");
        return Task.CompletedTask;
    }

    public Task HandleSaveAsync(object entity, string? documentId, bool isNew)
    {
        Saves.Add(documentId ?? "(new)");
        return Task.CompletedTask;
    }

    public Task HandleDeleteAsync(Type entityType, string documentId)
    {
        Deletes.Add(documentId);
        return Task.CompletedTask;
    }
}

public class InterceptedContext : SparkContext
{
    public IRavenQueryable<InterceptedNote> Notes => Session.Query<InterceptedNote>();
}

public static class InterceptedNoteModel
{
    public static EntityTypeFile For(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "InterceptedNote",
            ClrType = typeof(InterceptedNote).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
            ],
        }
    };
}
