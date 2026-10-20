using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
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
    private readonly IReplicationRequestObserver _approvers = Substitute.For<IReplicationRequestObserver>();
    private readonly ISparkGroupMembers _members = Substitute.For<ISparkGroupMembers>();
    private readonly ISecurityConfigurationLoader _security = Substitute.For<ISecurityConfigurationLoader>();
    private readonly ISparkLiveTopics _topics = Substitute.For<ISparkLiveTopics>();
    private readonly ISparkMailer _mailer = Substitute.For<ISparkMailer>();

    /// <summary>The group the apps bind the slot to (HR, Fleet: <c>"replication:approvers": ["Administrators"]</c>).</summary>
    private const string AdministratorsId = "a76a9b99-225d-4b3c-8985-cd29a9ddbd4e";

    public ReplicationApprovalTests()
    {
        _grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(true, [])));
        _sourceTypes.Resolve("ReplSourceCars").Returns(typeof(ReplSourceCar));
        _security.GetConfiguration().Returns(new SecurityConfiguration
        {
            Groups = new() { [AdministratorsId] = "Administrators" },
            Bindings = new(StringComparer.OrdinalIgnoreCase) { [ReplicationRequestArrived.ApproversSlot] = ["Administrators"] },
        });
        _members.GetMembersAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<SparkGroupMember>>(
        [
            new SparkGroupMember("users/ada", "ada", "ada@example.test", true, "nl-BE"),
            new SparkGroupMember("users/bob", "bob", "bob@example.test", false, null),
        ]));
    }

    private ReplicaSubscriptions Owner(string environment)
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        var services = new ServiceCollection().AddLogging().AddOptions()
            .AddSingleton(_outbox).AddSingleton(_approvers)
            .AddSingleton(_members).AddSingleton(_security).AddSingleton(_topics).AddSingleton(_mailer)
            .AddScoped<IReplicationRequestNotifier, ReplicationRequestNotifier>()
            .BuildServiceProvider();
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

    /// <summary>
    /// Red before the observer: a request waited in "Replication requests" and nobody was told. Now the
    /// <c>replication:approvers</c> slot hears of it once, after the commit, with the diff to decide on.
    /// </summary>
    [Fact]
    public async Task A_pending_request_tells_the_approvers_once()
    {
        var owner = Owner("Production");

        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        await _approvers.Received(1).OnReplicationRequestArrivedAsync(Arg.Is<ReplicationRequestArrived>(r =>
                r.RequestId == SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCar.ReplicationHash)
                && r.ConsumerModule == Consumer
                && r.SourceCollection == "ReplSourceCars"
                && r.TargetCollection == "ReplicaCars"
                && r.Hash == ReplicaCar.ReplicationHash
                && r.ApprovedHash == null
                && r.Diff!.Contains("+ OwnerName ← Owner.Name")),
            Arg.Any<CancellationToken>());
        ReplicationRequestArrived.ApproversSlot.Should().Be("replication:approvers");
    }

    /// <summary>The request is stored before anyone is told, so an approver who follows the notification finds it.</summary>
    [Fact]
    public async Task The_approvers_are_told_after_the_request_is_committed()
    {
        SparkReplicationRequest? seen = null;
        _approvers.OnReplicationRequestArrivedAsync(Arg.Any<ReplicationRequestArrived>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var session = Store.OpenAsyncSession();
                seen = await session.LoadAsync<SparkReplicationRequest>(call.Arg<ReplicationRequestArrived>().RequestId);
            });

        await Owner("Production").RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        seen.Should().NotBeNull();
        seen!.Status.Should().Be(ReplicationRequestStatuses.Pending);
    }

    /// <summary>A Development owner approves at once (WD16): there is nothing to decide, so nobody is told.</summary>
    [Fact]
    public async Task An_auto_approved_request_tells_nobody()
    {
        await Owner(Environments.Development).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        await _approvers.DidNotReceiveWithAnyArgs().OnReplicationRequestArrivedAsync(default!, default);
    }

    /// <summary>Q-W29: a new shape of a delivered replica is news too, and carries the shape that keeps flowing.</summary>
    [Fact]
    public async Task A_new_shape_tells_the_approvers_with_the_approved_hash()
    {
        var owner = Owner("Production");
        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item(ReplicaCarNarrow.Manifest, ReplicaCarNarrow.Script, ReplicaCarNarrow.Hash)], CancellationToken.None);
        await owner.ApproveAsync(SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCarNarrow.Hash), "users/admin", CancellationToken.None);
        _approvers.ClearReceivedCalls();

        await owner.RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        await _approvers.Received(1).OnReplicationRequestArrivedAsync(
            Arg.Is<ReplicationRequestArrived>(r => r.Hash == ReplicaCar.ReplicationHash && r.ApprovedHash == ReplicaCarNarrow.Hash),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A failing observer is logged; the registration (and the consumer's answer) does not depend on it.</summary>
    [Fact]
    public async Task A_failing_observer_does_not_fail_the_registration()
    {
        _approvers.OnReplicationRequestArrivedAsync(Arg.Any<ReplicationRequestArrived>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("mail server down")));

        var outcome = await Owner("Production").RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        outcome.StatusCode.Should().Be(200);
        outcome.Result.Items.Single().Status.Should().Be(ReplicaRegistrationStatus.Pending);
    }

    /// <summary>
    /// The default delivery, with no observer an app registered: the slot's members (the users holding a group the slot
    /// binds to) get a private <c>replication:requests</c> frame, and those with a confirmed address a mail.
    /// </summary>
    [Fact]
    public async Task By_default_the_slot_members_get_a_live_notice_and_a_mail()
    {
        await Owner("Production").RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        var id = SparkReplicationRequest.For(Consumer, "ReplicaCars", ReplicaCar.ReplicationHash);
        await _members.Received(1).GetMembersAsync(Arg.Is<IReadOnlyCollection<string>>(names => names.SequenceEqual(new[] { "Administrators" })), Arg.Any<CancellationToken>());
        _topics.Received(1).PublishPrivate("user:users/ada", ReplicationRequestNotifier.RequestsTopic, Arg.Is<ReplicationRequestFrame>(f => f.RequestId == id && f.Status == ReplicationRequestStatuses.Pending));
        _topics.Received(1).PublishPrivate("user:users/bob", ReplicationRequestNotifier.RequestsTopic, Arg.Any<object>());
        await _mailer.Received(1).SendAsync(Arg.Is<SparkMailRequest>(m => m.To == "ada@example.test" && m.Template == ReplicationRequestNotifier.RequestedTemplate && m.Culture == "nl-BE"), Arg.Any<CancellationToken>());
        await _mailer.DidNotReceive().SendAsync(Arg.Is<SparkMailRequest>(m => m.To == "bob@example.test"), Arg.Any<CancellationToken>());
    }

    /// <summary>An auto-approved request reaches no member either: no frame, no mail.</summary>
    [Fact]
    public async Task An_auto_approved_request_sends_no_frame_and_no_mail()
    {
        await Owner(Environments.Development).RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        _topics.DidNotReceiveWithAnyArgs().PublishPrivate(default!, default!, default!);
        await _mailer.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    /// <summary>A slot bound to no group tells nobody but the observers, and the registration still succeeds.</summary>
    [Fact]
    public async Task An_unbound_slot_reaches_only_the_observers()
    {
        _security.GetConfiguration().Returns(new SecurityConfiguration { Groups = new() { [AdministratorsId] = "Administrators" } });

        var outcome = await Owner("Production").RegisterAsync(Consumer, [ReplicationFixtures.Item()], CancellationToken.None);

        outcome.StatusCode.Should().Be(200);
        await _members.DidNotReceiveWithAnyArgs().GetMembersAsync(default!, default);
        _topics.DidNotReceiveWithAnyArgs().PublishPrivate(default!, default!, default!);
        await _approvers.Received(1).OnReplicationRequestArrivedAsync(Arg.Any<ReplicationRequestArrived>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The approvers' topic is private, and open to any signed-in user only for frames published to them.</summary>
    [Fact]
    public void The_requests_topic_is_private()
    {
        var policy = new ReplicationLiveTopicPolicy();
        policy.CanSubscribe(ReplicationRequestNotifier.RequestsTopic, new SparkLiveViewer("user:users/ada", "users/ada", null)).Should().BeFalse();
        policy.CanSubscribePrivate(ReplicationRequestNotifier.RequestsTopic, new SparkLiveViewer("user:users/ada", "users/ada", null)).Should().BeTrue();
        policy.CanSubscribePrivate(ReplicationRequestNotifier.RequestsTopic, new SparkLiveViewer("visitor:v1", null, "v1")).Should().BeFalse();
    }
}
