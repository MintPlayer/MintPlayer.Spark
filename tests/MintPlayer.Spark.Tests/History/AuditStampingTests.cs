using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.History;

/// <summary>
/// #271: History stamps each half of <see cref="IAuditable"/> on its own, and stamps a write a replica
/// forwarded (<c>Sync</c>) with the user the replica states (F2).
/// </summary>
/// <remarks>
/// Drives <c>HistoryInterceptor</c> directly: what is under test is the stamping decision, not the pipeline
/// around it, which <see cref="HistoryTests"/> covers end to end. The session is a substitute that reports
/// every entity as changed.
/// </remarks>
public class AuditStampingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Earlier = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static HistoryInterceptor Interceptor(string? userId, SyncScope? sync = null)
        => new(
            currentUser: new HiUser(userId),
            modelLoader: Substitute.For<IModelLoader>(),
            session: ChangedSession(),
            state: new HistoryRequestState(),
            timeProvider: new FixedTime(Now),
            syncInitiator: sync?.Initiator);

    /// <summary>
    /// A session that reports every entity as tracked and changed. NSubstitute's automatic stubs would
    /// answer an empty change vector and HasChanged == false, and the interceptor would then skip a
    /// save with a stored version as "nothing changed".
    /// </summary>
    private static IAsyncDocumentSession ChangedSession()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.Advanced.GetChangeVectorFor(Arg.Any<object>()).Returns("A:1-test");
        session.Advanced.HasChanged(Arg.Any<object>()).Returns(true);
        return session;
    }

    private static SaveContext Save(object entity, PersistentObjectOperation operation, object? before = null)
        => new()
        {
            EntityType = entity.GetType(),
            Operation = operation,
            PersistentObject = new PersistentObject { Name = entity.GetType().Name, ObjectTypeId = Guid.NewGuid() },
            Entity = entity,
            Before = before,
            Session = new object(),
        };

    [Fact]
    public async Task A_created_only_entity_gets_the_created_pair_on_create()
    {
        var entry = new StLogEntry();

        await Interceptor("users/alice").OnBeforeSaveAsync(Save(entry, PersistentObjectOperation.New));

        entry.CreatedBy.Should().Be("users/alice");
        entry.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_created_only_entity_keeps_the_stored_pair_on_every_later_save()
    {
        var entry = new StLogEntry { CreatedBy = "users/mallory", CreatedAt = Now };
        var stored = new StLogEntry { CreatedBy = "users/alice", CreatedAt = Earlier };

        await Interceptor("users/bob").OnBeforeSaveAsync(Save(entry, PersistentObjectOperation.Save, stored));

        entry.CreatedBy.Should().Be("users/alice");
        entry.CreatedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task A_modified_only_entity_gets_the_modified_pair()
    {
        var page = new StPage();

        await Interceptor("users/bob").OnBeforeSaveAsync(Save(page, PersistentObjectOperation.Save, new StPage()));

        page.ModifiedBy.Should().Be("users/bob");
        page.ModifiedAt.Should().Be(Now);
    }

    /// <summary>Before #271 a replica's write-back was never stamped, so the owner kept the previous editor.</summary>
    [Fact]
    public async Task A_sync_is_stamped_with_the_user_the_replica_states()
    {
        var note = new StNote { CreatedBy = "users/mallory", ModifiedBy = "users/previous", ModifiedAt = Earlier };
        var stored = new StNote { CreatedBy = "users/alice", CreatedAt = Earlier, ModifiedBy = "users/previous", ModifiedAt = Earlier };
        var sync = SyncScope.User("users/bob");

        // The caller of a sync is the replica's module certificate: no user id of its own.
        using (sync.Scope)
            await Interceptor(userId: null, sync).OnBeforeSaveAsync(Save(note, PersistentObjectOperation.Sync, stored));

        note.ModifiedBy.Should().Be("users/bob");
        note.ModifiedAt.Should().Be(Now);
        note.CreatedBy.Should().Be("users/alice", "authorship stays the stored value on a sync too");
        note.CreatedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task A_sync_insert_is_created_by_the_user_the_replica_states()
    {
        var note = new StNote();
        var sync = SyncScope.User("users/bob");

        using (sync.Scope)
            await Interceptor(userId: null, sync).OnBeforeSaveAsync(Save(note, PersistentObjectOperation.Sync));

        note.CreatedBy.Should().Be("users/bob");
        note.ModifiedBy.Should().Be("users/bob");
    }

    /// <summary>A replica older than #271, or an anonymous edit: left as the replica sent it, as before.</summary>
    [Fact]
    public async Task A_sync_that_states_no_user_is_not_stamped()
    {
        var note = new StNote { ModifiedBy = "users/previous", ModifiedAt = Earlier };

        await Interceptor(userId: null, SyncScope.User(null))
            .OnBeforeSaveAsync(Save(note, PersistentObjectOperation.Sync, new StNote()));

        note.ModifiedBy.Should().Be("users/previous");
        note.ModifiedAt.Should().Be(Earlier);
    }

    [Fact]
    public void The_startup_check_refuses_an_explicit_implementation()
    {
        AuditStartupCheck.MemberProblems(typeof(StExplicit)).Should().ContainSingle()
            .Which.Should().Contain("CreatedBy is not a public property");
        AuditStartupCheck.MemberProblems(typeof(StNote)).Should().BeEmpty();
        AuditStartupCheck.MemberProblems(typeof(StLogEntry)).Should().BeEmpty();
    }

    /// <summary>The request's sync initiator, as <c>SyncActionHandler</c> sets it around one write.</summary>
    internal sealed class SyncScope
    {
        public required SparkSyncInitiator Initiator { get; init; }
        public required IDisposable Scope { get; init; }

        public static SyncScope User(string? userId)
        {
            var initiator = new SparkSyncInitiator();
            return new() { Initiator = initiator, Scope = initiator.Begin(userId) };
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class StLogEntry : IAuditCreated
{
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public class StPage : IAuditModified
{
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

public class StNote : IAuditable
{
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

public class StExplicit : IAuditCreated
{
    string? IAuditCreated.CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}
