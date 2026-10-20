using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Consumer, <c>POST /spark/replicas/apply</c> (warehouse §4.2.3): the change-vector guard, tombstones, the source-module
/// poison check, the hash check with the Q-W29 fallback, and the durable replica-changed notification.
/// </summary>
public class ReplicaApplierTests : SparkTestDriver
{
    private const string Id = "ReplSourceCars/1-A";
    private const string V1 = "A:1-dbFleet";
    private const string V2 = "A:2-dbFleet";

    private readonly IMessageOutbox _outbox = Substitute.For<IMessageOutbox>();

    private ReplicaApplier Applier(bool withObserver = false)
    {
        var services = new ServiceCollection();
        if (withObserver)
            services.AddScoped(_ => Substitute.For<IReplicaChanged<ReplicaCar>>());
        return new ReplicaApplier(Store, ReplicationFixtures.Catalog(), _outbox, services.BuildServiceProvider(), TimeProvider.System, NullLogger<ReplicaApplier>.Instance);
    }

    private static ReplicaApplyRequest Push(string? vector, string plate = "1-ABC", bool deleted = false, string module = "Fleet", string hash = ReplicaCar.ReplicationHash) => new()
    {
        SourceModule = module,
        TargetCollection = "ReplicaCars",
        Hash = hash,
        Items =
        [
            new ReplicaItem
            {
                Id = Id,
                SourceChangeVector = vector,
                Deleted = deleted,
                Payload = deleted ? null : JsonSerializer.SerializeToElement(new { LicensePlate = plate, Model = "Golf", OwnerName = "Ann" }),
            },
        ],
    };

    private async Task<(ReplicaCar? Replica, string? Vector, string? Module, bool Tombstone)> ReadAsync()
    {
        using var session = Store.OpenAsyncSession();
        var replica = await session.LoadAsync<ReplicaCar>(Id);
        var tombstone = await session.LoadAsync<ReplicaTombstone>(ReplicaTombstone.For(Id));
        if (replica is null)
            return (null, null, null, tombstone is not null);
        var metadata = session.Advanced.GetMetadataFor(replica);
        return (replica, metadata[ReplicaMetadata.SourceChangeVector]?.ToString(), metadata[ReplicaMetadata.SourceModule]?.ToString(), tombstone is not null);
    }

    [Fact]
    public async Task A_newer_push_is_stored_with_its_source_stamp()
    {
        var (result, refusal) = await Applier().ApplyAsync("Fleet", Push(V1), CancellationToken.None);

        refusal.Should().BeNull();
        result!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Applied);
        var (replica, vector, module, _) = await ReadAsync();
        replica!.LicensePlate.Should().Be("1-ABC");
        replica.OwnerName.Should().Be("Ann");
        vector.Should().Be(V1);
        module.Should().Be("Fleet");
    }

    /// <summary>A resend after a lost answer, or a late push, is a no-op that still answers success.</summary>
    [Fact]
    public async Task An_equal_or_older_push_is_stale_and_changes_nothing()
    {
        await Applier().ApplyAsync("Fleet", Push(V2, plate: "newer"), CancellationToken.None);

        var (equal, _) = await Applier().ApplyAsync("Fleet", Push(V2, plate: "same-version"), CancellationToken.None);
        var (older, _) = await Applier().ApplyAsync("Fleet", Push(V1, plate: "older"), CancellationToken.None);

        equal!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Stale);
        older!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Stale);
        (await ReadAsync()).Replica!.LicensePlate.Should().Be("newer");
    }

    [Fact]
    public async Task A_deletion_leaves_a_tombstone_and_an_older_push_does_not_resurrect()
    {
        await Applier().ApplyAsync("Fleet", Push(V1), CancellationToken.None);
        await Applier().ApplyAsync("Fleet", Push(V2, deleted: true), CancellationToken.None);

        var (late, _) = await Applier().ApplyAsync("Fleet", Push(V1, plate: "late"), CancellationToken.None);

        late!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Stale);
        var (replica, _, _, tombstone) = await ReadAsync();
        replica.Should().BeNull();
        tombstone.Should().BeTrue();
    }

    /// <summary>Ids are unique across the sources of a collection: one held by another source is poison.</summary>
    [Fact]
    public async Task An_id_held_by_another_source_is_refused()
    {
        // The replica came from another source of the same collection.
        using (var session = Store.OpenAsyncSession())
        {
            var held = new ReplicaCar { LicensePlate = "rental-plate" };
            await session.StoreAsync(held, Id);
            session.Advanced.GetMetadataFor(held)[ReplicaMetadata.SourceModule] = "Rental";
            session.Advanced.GetMetadataFor(held)[ReplicaMetadata.SourceChangeVector] = "A:1-dbRental";
            await session.SaveChangesAsync();
        }

        var (result, refusal) = await Applier().ApplyAsync("Fleet", Push(V2), CancellationToken.None);

        refusal.Should().BeNull();
        result!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Refused);
        var (replica, _, module, _) = await ReadAsync();
        replica!.LicensePlate.Should().Be("rental-plate");
        module.Should().Be("Rental");
    }

    [Fact]
    public async Task A_module_that_is_no_source_is_forbidden()
    {
        var (_, refusal) = await Applier().ApplyAsync("HR", Push(V1, module: "HR"), CancellationToken.None);
        refusal!.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task A_hash_neither_current_nor_last_approved_is_a_manifest_mismatch()
    {
        var (_, refusal) = await Applier().ApplyAsync("Fleet", Push(V1, hash: "sha256:somethingelse"), CancellationToken.None);

        refusal!.StatusCode.Should().Be(409);
        refusal.Error.Should().Be("manifest-mismatch");
    }

    /// <summary>Q-W29: while a new shape waits for approval, pushes in the last approved shape keep landing.</summary>
    [Fact]
    public async Task The_last_approved_hash_is_accepted_while_a_new_shape_waits()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new ReplicaRegistrationState
            {
                Id = ReplicaRegistrationState.For("Fleet"),
                Owner = "Fleet",
                Items = { ["ReplicaCars"] = new ReplicaRegistrationAnswer { Status = ReplicaRegistrationStatus.Pending, ApprovedHash = ReplicaCarNarrow.Hash } },
            });
            await session.SaveChangesAsync();
        }

        var (result, refusal) = await Applier().ApplyAsync("Fleet", Push(V1, hash: ReplicaCarNarrow.Hash), CancellationToken.None);

        refusal.Should().BeNull();
        result!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Applied);
    }

    [Fact]
    public async Task A_replica_changed_message_is_enqueued_in_the_same_session_when_someone_observes()
    {
        await Applier(withObserver: true).ApplyAsync("Fleet", Push(V1), CancellationToken.None);

        await _outbox.Received(1).EnqueueAsync(Arg.Any<IAsyncDocumentSession>(),
            Arg.Is<ReplicaChangedMessage>(m => m.Id == Id && m.PreviousJson == null && m.CurrentJson != null && m.SourceChangeVector == V1),
            Arg.Any<BroadcastOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_enqueued_when_nobody_observes()
    {
        await Applier().ApplyAsync("Fleet", Push(V1), CancellationToken.None);

        await _outbox.DidNotReceiveWithAnyArgs().EnqueueAsync<ReplicaChangedMessage>(default!, default!, default, default);
    }

    [Fact]
    public async Task The_digest_lists_this_sources_replicas_with_their_vectors()
    {
        await Applier().ApplyAsync("Fleet", Push(V1), CancellationToken.None);

        var (page, refusal) = await Applier().DigestAsync("Fleet", "ReplicaCars", after: null, take: 10, CancellationToken.None);

        refusal.Should().BeNull();
        page!.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id, SourceChangeVector = V1 });
        page.Next.Should().BeNull();
    }
}
