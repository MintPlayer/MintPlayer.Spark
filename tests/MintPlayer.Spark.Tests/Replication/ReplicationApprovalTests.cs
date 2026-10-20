using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// The owner's registration and approval gate (warehouse §4.2.2, WD15, WD16, F16–F18). Red on e0c542f9: the owner
/// redeployed on every start, had no approval, and an administrator saw nothing.
/// </summary>
public class ReplicationApprovalTests : SparkTestDriver
{
    private const string Consumer = "HR";

    private readonly IModuleGrants _grants = Substitute.For<IModuleGrants>();
    private readonly IReplicaSourceTypes _sourceTypes = Substitute.For<IReplicaSourceTypes>();
    private readonly IMessageOutbox _outbox = Substitute.For<IMessageOutbox>();

    public ReplicationApprovalTests()
    {
        _grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(true, [])));
        _sourceTypes.Resolve("ReplSourceCars").Returns(typeof(ReplSourceCar));
    }

    private ReplicaSubscriptions Owner(string environment)
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        var services = new ServiceCollection().AddSingleton(_outbox).BuildServiceProvider();
        return new ReplicaSubscriptions(Store, _grants, _sourceTypes, host, services, TimeProvider.System, NullLogger<ReplicaSubscriptions>.Instance);
    }

    private Task DeployQueued(int times)
        => _outbox.Received(times).EnqueueAsync(Arg.Any<IAsyncDocumentSession>(), Arg.Is<ReplicaDeployMessage>(m => m.Consumer == Consumer),
            Arg.Any<BroadcastOptions?>(), Arg.Any<CancellationToken>());

    private async Task<SparkReplicationRequest> RequestAsync(string hash = ReplicaCar.ReplicationHash)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<SparkReplicationRequest>(SparkReplicationRequest.For(Consumer, "ReplicaCars", hash)))!;
    }

    private async Task<SparkReplicationSubscription?> SubscriptionAsync()
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<SparkReplicationSubscription>(SparkReplicationSubscription.For(Consumer, "ReplicaCars"));
    }

    /// <summary>WD16: a Development owner approves at once, recorded as automatic, and deploys.</summary>
    [Fact]
    public async Task A_Development_owner_auto_approves_and_deploys()
    {
        var outcome = await Owner(Environments.Development).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        outcome.StatusCode.Should().Be(200);
        outcome.Result.Items.Single().Status.Should().Be(ReplicaRegistrationStatus.Approved);
        var request = await RequestAsync();
        request.Status.Should().Be(ReplicationRequestStatuses.Approved);
        request.AutoApproved.Should().BeTrue();
        request.DecidedBy.Should().Be(SparkReplicationRequest.SystemApprover);
        (await SubscriptionAsync())!.ApprovedHash.Should().Be(ReplicaCar.ReplicationHash);
        await DeployQueued(1);
    }

    /// <summary>WD16: any other environment ("Testing", Production) waits for an administrator; nothing is delivered.</summary>
    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Any_other_environment_leaves_the_request_pending(string environment)
    {
        var outcome = await Owner(environment).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        outcome.Result.Items.Single().Status.Should().Be(ReplicaRegistrationStatus.Pending);
        outcome.Result.Items.Single().ApprovedHash.Should().BeNull();
        (await RequestAsync()).Status.Should().Be(ReplicationRequestStatuses.Pending);
        (await RequestAsync()).Diff.Should().Contain("+ OwnerName ← Owner.Name");
        (await SubscriptionAsync())!.IsDelivering.Should().BeFalse();
        await DeployQueued(0);
    }

    [Fact]
    public async Task Approving_a_pending_request_delivers_it()
    {
        var owner = Owner("Production");
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        var decision = await owner.ApproveAsync(SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCar.ReplicationHash), "users/admin", CancellationToken.None);

        decision.Should().Be(ReplicationDecisionOutcome.Done);
        var request = await RequestAsync();
        request.Status.Should().Be(ReplicationRequestStatuses.Approved);
        request.AutoApproved.Should().BeFalse();
        request.DecidedBy.Should().Be("users/admin");
        (await SubscriptionAsync())!.IsDelivering.Should().BeTrue();
        await DeployQueued(1);
    }

    [Fact]
    public async Task Rejecting_suspends_and_a_repost_of_the_rejected_shape_is_refused()
    {
        var owner = Owner("Production");
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        (await owner.RejectAsync(SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCar.ReplicationHash), "users/admin", null, CancellationToken.None))
            .Should().Be(ReplicationDecisionOutcome.Done);
        var again = await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        (await RequestAsync()).Status.Should().Be(ReplicationRequestStatuses.Rejected);
        (await SubscriptionAsync())!.State.Should().Be(ReplicationSubscriptionStates.Suspended);
        again.Result.Items.Single().Status.Should().Be(ReplicaRegistrationStatus.Refused);
    }

    /// <summary>WD15 red→green: re-posting the approved hash is "unchanged", and nothing is redeployed.</summary>
    [Fact]
    public async Task The_same_hash_again_is_unchanged_and_not_redeployed()
    {
        var owner = Owner(Environments.Development);
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        var again = await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        again.Result.Items.Single().Status.Should().Be(ReplicaRegistrationStatus.Unchanged);
        await DeployQueued(1);
    }

    /// <summary>
    /// Q-W29: a consumer upgraded with a new shape waits for approval while the approved shape keeps flowing; the
    /// administrator sees the diff against the shape delivered now.
    /// </summary>
    [Fact]
    public async Task A_new_shape_waits_while_the_approved_one_keeps_flowing()
    {
        var owner = Owner("Production");
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item(ReplicaCarNarrow.Manifest, ReplicaCarNarrow.Script, ReplicaCarNarrow.Hash)], CancellationToken.None);
        await owner.ApproveAsync(SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCarNarrow.Hash), "users/admin", CancellationToken.None);

        var upgraded = await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        var item = upgraded.Result.Items.Single();
        item.Status.Should().Be(ReplicaRegistrationStatus.Pending);
        item.ApprovedHash.Should().Be(ReplicaCarNarrow.Hash);
        (await SubscriptionAsync())!.IsDelivering.Should().BeTrue();
        var request = await RequestAsync();
        request.Diff.Should().Be("+ OwnerName ← Owner.Name");
        request.ApprovedHash.Should().Be(ReplicaCarNarrow.Hash);
    }

    /// <summary>F16 closed by construction: a script that is not the rendering of its manifest is never stored.</summary>
    [Fact]
    public async Task A_consumer_authored_script_is_refused_and_never_stored()
    {
        var item = ReplicationFixtures.Item(script: "loadToReplicaCars(loadDocument('Users/1'));");

        var outcome = await Owner(Environments.Development).RegisterAsync(Consumer, [item], CancellationToken.None);

        outcome.StatusCode.Should().Be(400);
        (await SubscriptionAsync()).Should().BeNull();
        await DeployQueued(0);
    }

    [Fact]
    public async Task A_manifest_with_a_non_identifier_path_is_refused()
    {
        var item = ReplicationFixtures.Item(manifest: ReplicaCar.ReplicationManifest.Replace("Owner.Name", "Owner.Name;x"));

        (await Owner(Environments.Development).RegisterAsync(Consumer, [item], CancellationToken.None)).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task No_Replicate_grant_is_forbidden()
    {
        _grants.CheckAsync(Consumer, "ReplSourceCars", Arg.Any<ReplicaManifest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new ReplicaGrant(false, [])));

        var outcome = await Owner(Environments.Development).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        outcome.StatusCode.Should().Be(403);
        await DeployQueued(0);
    }

    /// <summary>F17, read side: a manifest field the module may not read is refused by name.</summary>
    [Fact]
    public async Task A_field_without_the_read_right_is_refused_by_name()
    {
        _grants.CheckAsync(Consumer, "ReplSourceCars", Arg.Any<ReplicaManifest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new ReplicaGrant(true, ["Owner"])));

        var outcome = await Owner(Environments.Development).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        outcome.StatusCode.Should().Be(403);
        outcome.Result.Error.Should().Contain("Owner");
    }

    [Fact]
    public async Task An_unknown_source_collection_is_not_found()
    {
        _sourceTypes.Resolve("ReplSourceCars").Returns((Type?)null);

        (await Owner(Environments.Development).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None)).StatusCode.Should().Be(404);
    }

    /// <summary>F18: what the consumer no longer registers is withdrawn and its delivery removed.</summary>
    [Fact]
    public async Task A_replica_missing_from_the_post_is_withdrawn()
    {
        var owner = Owner(Environments.Development);
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        var outcome = await owner.RegisterAsync(Consumer, [], CancellationToken.None);

        outcome.Result.Withdrawn.Should().BeEquivalentTo(["ReplicaCars"]);
        (await SubscriptionAsync())!.State.Should().Be(ReplicationSubscriptionStates.Withdrawn);
        await DeployQueued(2);
    }

    [Fact]
    public async Task Approval_rechecks_the_grant_and_suspends_when_it_is_gone()
    {
        var owner = Owner("Production");
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);
        _grants.CheckAsync(Consumer, "ReplSourceCars", Arg.Any<ReplicaManifest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new ReplicaGrant(false, [])));

        var decision = await owner.ApproveAsync(SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCar.ReplicationHash), "users/admin", CancellationToken.None);

        decision.Should().Be(ReplicationDecisionOutcome.NotGranted);
        (await SubscriptionAsync())!.State.Should().Be(ReplicationSubscriptionStates.Suspended);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_decided_again()
    {
        var owner = Owner(Environments.Development);
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        (await owner.RejectAsync(SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCar.ReplicationHash), "users/admin", null, CancellationToken.None))
            .Should().Be(ReplicationDecisionOutcome.NotPending);
    }
}
