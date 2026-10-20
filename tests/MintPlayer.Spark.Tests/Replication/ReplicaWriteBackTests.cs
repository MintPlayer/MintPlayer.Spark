using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Consumer write-back (warehouse §4.2.5, owner decisions 2026-10-20): synchronous first, queued only when the owner
/// does not answer, per-property bases, one pending change per row, and never a local write. Red on e0c542f9/WIP: the
/// replica was saved locally first and the SyncAction written after the commit in a new session
/// (<c>ReplicationInterceptor.cs:16,28-34</c>, <c>SyncActionInterceptor.cs:149-164</c>), so a crash lost the edit (F13).
/// </summary>
public class ReplicaWriteBackTests : SparkTestDriver
{
    private const string Id = "ReplSourceCars/1-A";

    private readonly ISyncActionSender _sender = Substitute.For<ISyncActionSender>();
    private readonly IEntityMapper _mapper = Substitute.For<IEntityMapper>();
    private readonly IMessageOutbox _outbox = Substitute.For<IMessageOutbox>();
    private readonly ISparkCurrentUser _user = Substitute.For<ISparkCurrentUser>();

    public ReplicaWriteBackTests()
    {
        _user.IsAuthenticated.Returns(true);
        _user.Id.Returns("SparkUsers/alice");
        _mapper.ToEntity(Arg.Any<PersistentObject>()).Returns(new ReplicaCar { LicensePlate = "NEW-1", Model = "Golf", OwnerName = "Ann" });
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using var session = Store.OpenAsyncSession();
        var replica = new ReplicaCar { LicensePlate = "OLD-1", Model = "Golf", OwnerName = "Ann" };
        await session.StoreAsync(replica, Id);
        session.Advanced.GetMetadataFor(replica)[ReplicaMetadata.SourceModule] = "Fleet";
        session.Advanced.GetMetadataFor(replica)[ReplicaMetadata.SourceChangeVector] = "A:1-dbFleet";
        await session.SaveChangesAsync();
    }

    private ReplicaWriteBackInterceptor Interceptor(bool writeBack = true)
    {
        var options = ReplicationFixtures.Options("HR");
        return new ReplicaWriteBackInterceptor(ReplicationFixtures.Catalog(options), Store, _mapper, _sender, _outbox,
            Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System, NullLogger<ReplicaWriteBackInterceptor>.Instance, _user);
    }

    private void Owner(HttpStatusCode? status, SyncApplyResult? result = null)
        => _sender.SendAsync(Arg.Any<string>(), Arg.Any<SyncApplyRequest>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ModuleCallResult<SyncApplyResult>(status, result, status is null ? "unreachable" : "")));

    private static SaveReplacementContext Edit(IAsyncDocumentSession session, string attribute = "LicensePlate", string value = "NEW-1")
    {
        var po = new PersistentObject { Id = Id, Name = "ReplicaCar", ObjectTypeId = Guid.NewGuid() };
        po.AddAttribute(new PersistentObjectAttribute { Name = attribute, Value = value, IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "Model", Value = "Golf", IsValueChanged = false });
        return new SaveReplacementContext
        {
            EntityType = typeof(ReplicaCar),
            Session = session,
            Operation = PersistentObjectOperation.Save,
            PersistentObject = po,
            PersistentObjectJson = "{}",
            Id = Id,
            Before = new ReplicaCar { Id = Id, LicensePlate = "OLD-1", Model = "Golf", OwnerName = "Ann" },
        };
    }

    private async Task<(ReplicaCar Replica, SyncPendingMarker? Marker, List<SparkSyncAction> Actions)> ReadAsync()
    {
        using var session = Store.OpenAsyncSession();
        var replica = (await session.LoadAsync<ReplicaCar>(Id))!;
        var marker = await session.LoadAsync<SyncPendingMarker>(SyncPendingMarker.For(Id));
        var actions = (await session.Advanced.LoadStartingWithAsync<SparkSyncAction>("SparkSyncActions/")).ToList();
        return (replica, marker, actions);
    }

    [Fact]
    public async Task Applied_by_the_owner_within_the_save_answers_replication_and_leaves_the_replica_untouched()
    {
        Owner(HttpStatusCode.OK, new SyncApplyResult { Outcome = SyncApplyOutcome.Applied });
        using var session = Store.OpenAsyncSession();

        var receipt = await Interceptor().ReplaceAsync(Edit(session));
        await session.SaveChangesAsync();

        receipt!.Kind.Should().Be("replication");
        var (replica, marker, actions) = await ReadAsync();
        replica.LicensePlate.Should().Be("OLD-1", "the replica changes only when the owner's push arrives");
        marker!.SyncActionId.Should().BeNull();
        actions.Should().BeEmpty();
    }

    /// <summary>The changed property carries its base (the replica's value) and its new value, by the owner's name.</summary>
    [Fact]
    public async Task The_owner_gets_the_changed_property_with_its_base_and_the_initiator()
    {
        Owner(HttpStatusCode.OK, new SyncApplyResult { Outcome = SyncApplyOutcome.Applied });
        using var session = Store.OpenAsyncSession();

        await Interceptor().ReplaceAsync(Edit(session));

        await _sender.Received(1).SendAsync("Fleet", Arg.Is<SyncApplyRequest>(r =>
            r.RequestingModule == "HR" && r.SourceCollection == "ReplSourceCars" && r.DocumentId == Id && r.InitiatorId == "SparkUsers/alice"
            && r.Changes.Count == 1 && r.Changes[0].Property == "LicensePlate"
            && r.Changes[0].Base.GetString() == "OLD-1" && r.Changes[0].Value.GetString() == "NEW-1"),
            TimeSpan.FromSeconds(5), Arg.Any<CancellationToken>());
    }

    /// <summary>F13 red→green: an unreachable owner queues the edit and its message in the save's own commit.</summary>
    [Fact]
    public async Task An_unreachable_owner_queues_the_edit_in_the_same_commit()
    {
        Owner(null);
        using var session = Store.OpenAsyncSession();

        var receipt = await Interceptor().ReplaceAsync(Edit(session));
        await _outbox.Received(1).EnqueueAsync(session, Arg.Any<SyncActionDispatchMessage>(), Arg.Any<BroadcastOptions?>(), Arg.Any<CancellationToken>());
        await session.SaveChangesAsync();

        receipt!.Kind.Should().Be("replication-pending");
        var (replica, marker, actions) = await ReadAsync();
        replica.LicensePlate.Should().Be("OLD-1");
        var action = actions.Should().ContainSingle().Which;
        action.Status.Should().Be(SyncActionStatuses.Pending);
        action.InitiatorId.Should().Be("SparkUsers/alice");
        action.OwnerModule.Should().Be("Fleet");
        action.Changes.Single().BaseJson.Should().Be("\"OLD-1\"");
        receipt.ReferenceId.Should().Be(action.Id);
        marker!.SyncActionId.Should().Be(action.Id);
    }

    [Fact]
    public async Task A_5xx_from_the_owner_queues_the_edit_too()
    {
        Owner(HttpStatusCode.ServiceUnavailable);
        using var session = Store.OpenAsyncSession();

        (await Interceptor().ReplaceAsync(Edit(session)))!.Kind.Should().Be("replication-pending");
    }

    /// <summary>A conflict answers like a local save's 409, naming the replica's property; nothing is queued.</summary>
    [Fact]
    public async Task A_conflict_at_the_owner_is_the_409_a_person_gets()
    {
        Owner(HttpStatusCode.Conflict, new SyncApplyResult { Outcome = SyncApplyOutcome.Conflict, Reason = "changed", Attributes = ["LicensePlate"] });
        using var session = Store.OpenAsyncSession();

        var act = () => Interceptor().ReplaceAsync(Edit(session)).AsTask();

        var conflict = (await act.Should().ThrowAsync<SparkConflictException>()).Which;
        conflict.Reason.Should().Be(SparkConflictException.Changed);
        conflict.Attributes.Should().BeEquivalentTo(["LicensePlate"]);
        conflict.UserMessage.Should().Contain("Fleet");
    }

    [Theory]
    [InlineData(SyncRefusalKind.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(SyncRefusalKind.Invalid, HttpStatusCode.UnprocessableEntity)]
    public async Task A_refusal_at_the_owner_is_a_validation_answer(SyncRefusalKind kind, HttpStatusCode status)
    {
        Owner(status, new SyncApplyResult { Outcome = SyncApplyOutcome.Refused, Refusal = kind, Reason = "no", Attributes = ["LicensePlate"] });
        using var session = Store.OpenAsyncSession();

        var act = () => Interceptor().ReplaceAsync(Edit(session)).AsTask();

        (await act.Should().ThrowAsync<SparkValidationException>()).Which.AttributeName.Should().Be("LicensePlate");
    }

    /// <summary>Q-W28: a second edit while one is pending is refused locally.</summary>
    [Fact]
    public async Task A_second_edit_while_one_is_pending_is_refused()
    {
        Owner(null);
        using (var first = Store.OpenAsyncSession())
        {
            await Interceptor().ReplaceAsync(Edit(first));
            await first.SaveChangesAsync();
        }

        using var second = Store.OpenAsyncSession();
        var act = () => Interceptor().ReplaceAsync(Edit(second, value: "NEW-2")).AsTask();

        (await act.Should().ThrowAsync<SparkValidationException>()).Which.Message.Should().Be("A change to this row is still pending at Fleet.");
        await _sender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<SyncApplyRequest>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>After an applied edit the row stays pending until the owner's delivery rewrites the replica.</summary>
    [Fact]
    public async Task An_applied_edit_stays_pending_until_the_delivery_rewrites_the_replica()
    {
        Owner(HttpStatusCode.OK, new SyncApplyResult { Outcome = SyncApplyOutcome.Applied });
        using (var first = Store.OpenAsyncSession())
        {
            await Interceptor().ReplaceAsync(Edit(first));
            await first.SaveChangesAsync();
        }

        using (var blocked = Store.OpenAsyncSession())
        {
            var act = () => Interceptor().ReplaceAsync(Edit(blocked, value: "NEW-2")).AsTask();
            await act.Should().ThrowAsync<SparkValidationException>();
        }

        // The delivery rewrites the replica (a new change vector of its own).
        using (var push = Store.OpenAsyncSession())
        {
            (await push.LoadAsync<ReplicaCar>(Id))!.LicensePlate = "NEW-1";
            await push.SaveChangesAsync();
        }

        using var after = Store.OpenAsyncSession();
        (await Interceptor().ReplaceAsync(Edit(after, value: "NEW-2")))!.Kind.Should().Be("replication");
    }

    [Fact]
    public async Task A_field_read_from_a_nested_path_cannot_be_written_back()
    {
        using var session = Store.OpenAsyncSession();

        var act = () => Interceptor().ReplaceAsync(Edit(session, attribute: "OwnerName", value: "Bob")).AsTask();

        (await act.Should().ThrowAsync<SparkValidationException>()).Which.AttributeName.Should().Be("OwnerName");
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_replica_is_never_created_here()
    {
        using var session = Store.OpenAsyncSession();
        var context = Edit(session);
        var create = new SaveReplacementContext
        {
            EntityType = context.EntityType,
            Session = session,
            Operation = PersistentObjectOperation.New,
            PersistentObject = context.PersistentObject,
            PersistentObjectJson = "{}",
        };

        var act = () => Interceptor().ReplaceAsync(create).AsTask();

        await act.Should().ThrowAsync<SparkValidationException>();
    }

    [Fact]
    public async Task The_pending_badge_is_on_the_loaded_row()
    {
        Owner(null);
        using (var first = Store.OpenAsyncSession())
        {
            await Interceptor().ReplaceAsync(Edit(first));
            await first.SaveChangesAsync();
        }

        using var session = Store.OpenAsyncSession();
        var po = new PersistentObject { Id = Id, Name = "ReplicaCar", ObjectTypeId = Guid.NewGuid() };
        await Interceptor().OnAfterLoadAsync(new LoadContext { EntityType = typeof(ReplicaCar), Session = session, PersistentObject = po });

        var badge = (Dictionary<string, object?>)po.Metadata![ReplicaWriteBackInterceptor.MetadataKey]!;
        badge["pending"].Should().Be(true);
        badge["owner"].Should().Be("Fleet");
        badge["own"].Should().Be(true);
    }
}
