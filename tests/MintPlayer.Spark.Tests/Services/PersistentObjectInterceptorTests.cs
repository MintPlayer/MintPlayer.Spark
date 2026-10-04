using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #460 item 1 and #482 — persistence interceptors run by <c>DatabaseAccess</c>,
/// including spike S4: a delete replacement nothing can defeat (now structural: the Actions class has
/// no delete method), surfaced to every after-delete interceptor so replication forwards a save rather than a
/// hard delete.
/// </summary>
public class PersistentObjectInterceptorTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46a1c7e0-4600-4600-4600-46a1c7e04600");

    private SparkEndpointFactory<InterceptedContext> factory = null!;
    private readonly InterceptionLog log = new();
    private readonly ReplicationLog sync = new();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(NoteTypeId)],
            configureServices: services =>
            {
                services.AddSingleton(log);
                services.AddSingleton(sync);
            },
            configureSpark: spark => spark
                .AddInterceptor<ReplacingDeleteInterceptor>()
                .AddInterceptor<StampingInterceptor>()
                .AddInterceptor<StampingInterceptor>()     // twice: a no-op
                .AddInterceptor<RecordingReplicationInterceptor>());
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedAsync() => SeedAsync(async session =>
        await session.StoreAsync(new InterceptedNote { Id = "InterceptedNotes/1", Title = "first" }));

    [Fact]
    public async Task S4_a_replaced_delete_is_decided_first_and_replicates_as_a_save()
    {
        await SeedAsync();
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            await db.DeletePersistentObjectAsync(NoteTypeId, "InterceptedNotes/1");
        }

        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<InterceptedNote>("InterceptedNotes/1");
        stored.Should().NotBeNull("the replacement kept the document");
        stored!.IsDeleted.Should().BeTrue("the replacement's changes to the tracked entity were saved");

        log.Entries.Should().Equal(
            "replace.ReplaceAsync",
            "replace.OnBeforeDeleteAsync:replaced",
            "stamp.OnBeforeDeleteAsync:replaced",
            "actions.OnBeforeDeleteAsync:replaced",
            "replace.OnAfterDeleteAsync",
            "stamp.OnAfterDeleteAsync");

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
            "replace.OnBeforeDeleteAsync:deleted",
            "stamp.OnBeforeDeleteAsync:deleted",
            "actions.OnBeforeDeleteAsync:deleted",
            "replace.OnAfterDeleteAsync",
            "stamp.OnAfterDeleteAsync");
        sync.Deletes.Should().Equal("InterceptedNotes/1");
        sync.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_interceptors_run_in_registration_order_with_the_actions_interceptor_last()
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
            "replace.OnBeforeSaveAsync:New",
            "stamp.OnBeforeSaveAsync:New",
            "actions.OnBeforeSaveAsync",
            "replace.OnAfterSaveAsync",
            "stamp.OnAfterSaveAsync");

        using var verify = Store.OpenAsyncSession();
        var stored = await verify.Query<InterceptedNote>().Customize(c => c.WaitForNonStaleResults()).SingleAsync();
        stored.Stamp.Should().Be("stamped", "a before-save interceptor's mutation is what gets written");
    }

    [Fact]
    public async Task A_failing_after_interceptor_neither_fails_the_committed_save_nor_skips_the_others()
    {
        log.FailAfterSave = true;
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var po = new PersistentObject { ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "kept", IsValueChanged = true });
            var saved = await db.SavePersistentObjectAsync(po);
            saved.Id.Should().NotBeNullOrEmpty();
        }

        log.Entries.Should().Contain("stamp.OnAfterSaveAsync", "the interceptor after the failing one still ran");
        using var verify = Store.OpenAsyncSession();
        (await verify.Query<InterceptedNote>().Customize(c => c.WaitForNonStaleResults()).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_cancel_writes_nothing_and_runs_no_after_interceptor()
    {
        await SeedAsync();
        log.CancelDelete = true;
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var act = () => db.DeletePersistentObjectAsync(NoteTypeId, "InterceptedNotes/1");
            await act.Should().ThrowAsync<SparkCancelException>();
        }

        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<InterceptedNote>("InterceptedNotes/1");
        stored.Should().NotBeNull();
        stored!.IsDeleted.Should().BeFalse("the replacement's mark was taken back with the cancel");
        log.Entries.Should().NotContain(e => e.Contains("OnAfter"));
        sync.Saves.Should().BeEmpty();
        sync.Deletes.Should().BeEmpty();
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

/// <summary>An Actions class that is also its type's interceptor — run without registration, after the registered interceptors.</summary>
public class InterceptedNoteActions : DefaultPersistentObjectActions<InterceptedNote>,
    IBeforeSave<InterceptedNote>, IBeforeDelete<InterceptedNote>
{
    private readonly InterceptionLog log;

    public InterceptedNoteActions(IEntityMapper entityMapper, InterceptionLog log) : base(entityMapper) => this.log = log;

    public ValueTask OnBeforeSaveAsync(InterceptedNote entity, SaveContext context)
    {
        log.Entries.Add("actions.OnBeforeSaveAsync");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnBeforeDeleteAsync(InterceptedNote entity, DeleteContext context)
    {
        log.Entries.Add($"actions.OnBeforeDeleteAsync:{(context.IsReplaced ? "replaced" : "deleted")}");
        if (log.CancelDelete)
            throw new SparkCancelException();
        return ValueTask.CompletedTask;
    }
}

public sealed class InterceptionLog
{
    public List<string> Entries { get; } = [];
    public bool FailAfterSave { get; set; }
    public bool CancelDelete { get; set; }
}

public sealed class ReplacingDeleteInterceptor(InterceptionLog log) : IDeleteReplacement, IBeforeSave, IAfterSave, IBeforeDelete, IAfterDelete
{
    public bool AppliesTo(Type entityType) => entityType == typeof(InterceptedNote);

    public ValueTask<bool> ReplaceAsync(DeleteContext context)
    {
        log.Entries.Add("replace.ReplaceAsync");
        ((InterceptedNote)context.Entity).IsDeleted = true;
        return ValueTask.FromResult(true);
    }

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        log.Entries.Add($"replace.OnBeforeSaveAsync:{context.Operation}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterSaveAsync(SaveContext context)
    {
        log.Entries.Add("replace.OnAfterSaveAsync");
        if (log.FailAfterSave)
            throw new InvalidOperationException("after-interceptor failure");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        log.Entries.Add($"replace.OnBeforeDeleteAsync:{(context.IsReplaced ? "replaced" : "deleted")}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        log.Entries.Add("replace.OnAfterDeleteAsync");
        return ValueTask.CompletedTask;
    }
}

public sealed class StampingInterceptor(InterceptionLog log) : IBeforeSave, IAfterSave, IBeforeDelete, IAfterDelete, IAfterLoad
{
    public bool AppliesTo(Type entityType) => entityType == typeof(InterceptedNote);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        log.Entries.Add($"stamp.OnBeforeSaveAsync:{context.Operation}");
        ((InterceptedNote)context.Entity).Stamp = "stamped";
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterSaveAsync(SaveContext context)
    {
        log.Entries.Add("stamp.OnAfterSaveAsync");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        log.Entries.Add($"stamp.OnBeforeDeleteAsync:{(context.IsReplaced ? "replaced" : "deleted")}");
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

public sealed class ReplicationLog
{
    public List<string> Saves { get; } = [];
    public List<string> Deletes { get; } = [];
}

/// <summary>What the Replication package's interceptor forwards, recorded: a replaced delete as a save.</summary>
public sealed class RecordingReplicationInterceptor(ReplicationLog sync) : IAfterSave, IAfterDelete
{
    public bool AppliesTo(Type entityType) => entityType == typeof(InterceptedNote);

    public ValueTask OnAfterSaveAsync(SaveContext context)
    {
        sync.Saves.Add(context.Id ?? "(new)");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        (context.IsReplaced ? sync.Saves : sync.Deletes).Add(context.Id);
        return ValueTask.CompletedTask;
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
