using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #482, D17 — durable after-commit hooks: one unit of work per row and hook, stored in the write's own
/// commit, delivered later with the payload (never the entity). A refused or cancelled write commits
/// none. Delivery here is <see cref="TestAfterCommitOutbox"/>, which stores exactly as Messaging does.
/// </summary>
public class DurableAfterCommitHookTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46a1c7e0-4600-4600-4600-46a1c7e04682");

    private SparkEndpointFactory<InterceptedContext> factory = null!;
    private readonly InterceptionLog log = new();
    private readonly CommittedLog committed = new();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(NoteTypeId)],
            configureServices: services =>
            {
                services.AddSingleton(log);
                services.AddSingleton(committed);
                services.AddTestAfterCommitOutbox();
            },
            configureSpark: spark => spark
                .AddHook<FactRecordingHook>()
                .AddHook<RecordingCommittedHook>());
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private TestAfterCommitOutbox Outbox => factory.GetService<TestAfterCommitOutbox>();

    private Task<int> DrainAsync() => Outbox.DrainAsync(factory.GetService<IServiceProvider>());

    private async Task<PersistentObject> SaveAsync(string title, string? id = null)
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var po = new PersistentObject { Id = id, ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = title, IsValueChanged = true });
        return await db.SavePersistentObjectAsync(po);
    }

    [Fact]
    public async Task A_committed_save_runs_its_durable_hook_only_when_delivered_with_the_payload_and_facts()
    {
        var saved = await SaveAsync("one");

        committed.Changes.Should().BeEmpty("nothing runs in the request");
        (await DrainAsync()).Should().Be(1, "one row, one durable hook");

        var change = committed.Changes.Should().ContainSingle().Which;
        change.Operation.Should().Be(PersistentObjectOperation.New);
        change.IsNew.Should().BeTrue();
        change.Id.Should().Be(saved.Id);
        change.EntityType.Should().Be(typeof(InterceptedNote).FullName);
        change.PreviousChangeVector.Should().BeNull("a create has no previous version");
        change.Facts["Title"].Should().Be("one", "a before-hook's facts travel with the payload");
    }

    [Fact]
    public async Task An_edit_carries_the_version_it_replaced()
    {
        var created = await SaveAsync("one");
        await SaveAsync("two", created.Id);

        await DrainAsync();
        committed.Changes.Select(c => c.Operation).Should().Equal(PersistentObjectOperation.New, PersistentObjectOperation.Save);
        committed.Changes[1].PreviousChangeVector.Should().Be(created.Etag);
    }

    [Fact]
    public async Task A_refused_save_commits_no_durable_work()
    {
        var act = () => SaveAsync("refuse");
        await act.Should().ThrowAsync<SparkValidationException>();

        Outbox.Enqueued.Should().BeEmpty("the hook refused before the framework enqueued");
        (await DrainAsync()).Should().Be(0);
        committed.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_save_commits_no_durable_work()
    {
        var created = await SaveAsync("one");
        await DrainAsync();
        committed.Changes.Clear();

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var po = new PersistentObject { Id = created.Id, Etag = "A:1-stale", ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "two", IsValueChanged = true });
            var act = () => db.SavePersistentObjectAsync(po);
            await act.Should().ThrowAsync<SparkConcurrencyException>();
        }

        (await DrainAsync()).Should().Be(0, "nothing of a refused write commits");
        committed.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bulk_delete_refused_on_a_later_row_takes_back_the_work_enqueued_for_earlier_rows()
    {
        var first = await SaveAsync("one");
        var kept = await SaveAsync("keep");
        await DrainAsync();
        committed.Changes.Clear();

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var act = () => db.DeletePersistentObjectsAsync(NoteTypeId, [first.Id!, kept.Id!]);
            await act.Should().ThrowAsync<SparkValidationException>();
        }

        Outbox.Enqueued.Should().HaveCount(1, "the first row's work was stored before the second row was refused");
        (await DrainAsync()).Should().Be(0, "it was in the commit that never happened");
        committed.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_delete_commits_no_durable_work()
    {
        var created = await SaveAsync("one");
        await DrainAsync();
        committed.Changes.Clear();
        log.CancelDelete = true;

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var act = () => db.DeletePersistentObjectAsync(NoteTypeId, created.Id!);
            await act.Should().ThrowAsync<SparkCancelException>();
        }

        (await DrainAsync()).Should().Be(0);
        committed.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_delete_runs_the_durable_delete_hook_with_the_reason_and_the_previous_version()
    {
        var created = await SaveAsync("one");
        await DrainAsync();
        committed.Changes.Clear();

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            await db.DeletePersistentObjectsAsync(NoteTypeId, [created.Id!], new SparkBulkDeleteContext { Reason = "spam" });
        }

        (await DrainAsync()).Should().Be(1);
        var change = committed.Changes.Should().ContainSingle().Which;
        change.Operation.Should().Be(PersistentObjectOperation.Delete);
        change.IsReplaced.Should().BeFalse();
        change.Reason.Should().Be("spam");
        change.PreviousChangeVector.Should().Be(created.Etag);
    }

    [Fact]
    public async Task A_durable_hook_without_an_outbox_is_a_startup_error()
    {
        var act = async () =>
        {
            await using var bare = new SparkEndpointFactory<InterceptedContext>(
                Store,
                [InterceptedNoteModel.For(NoteTypeId)],
                configureServices: services => services.AddSingleton(log).AddSingleton(committed),
                configureSpark: spark => spark.AddHook<RecordingCommittedHook>());
            using var _ = bare.CreateScope();
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("spark.AddMessaging()");
    }
}

public sealed class CommittedLog
{
    public List<SparkCommittedChange> Changes { get; } = [];
}

/// <summary>Records a fact for the durable hook, refuses a save titled "refuse" and a delete of a row titled "keep".</summary>
public sealed class FactRecordingHook : IBeforeSave<InterceptedNote>, IBeforeDelete<InterceptedNote>
{
    public ValueTask OnBeforeDeleteAsync(InterceptedNote entity, DeleteContext context)
        => entity.Title == "keep" ? throw new SparkValidationException("kept") : ValueTask.CompletedTask;

    public ValueTask OnBeforeSaveAsync(InterceptedNote entity, SaveContext context)
    {
        if (entity.Title == "refuse")
            throw new SparkValidationException("refused", "Title");
        context.Facts["Title"] = entity.Title;
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingCommittedHook(CommittedLog log) : IAfterSaveCommitted<InterceptedNote>, IAfterDeleteCommitted<InterceptedNote>
{
    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        log.Changes.Add(change);
        return Task.CompletedTask;
    }

    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        log.Changes.Add(change);
        return Task.CompletedTask;
    }
}
