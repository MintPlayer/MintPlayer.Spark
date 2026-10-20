using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Consumer registration (warehouse §4.2.2, WD15): post only when the set hash is not settled; retry an owner that is
/// not there; dead-letter a settled refusal. Replaces <c>EtlScriptDeploymentRecipientTests</c>.
/// </summary>
public class ReplicaRegistrationRecipientTests : SparkTestDriver
{
    private readonly List<HttpRequestMessage> _requests = [];

    private ReplicaRegistrationRecipient Recipient(Func<HttpRequestMessage, Task<HttpResponseMessage>> owner, params string[] registered)
        => new(ReplicationFixtures.Catalog(), Store, ReplicationFixtures.Directory(registered), ReplicationFixtures.Http(owner, _requests),
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("HR")), TimeProvider.System, NullLogger<ReplicaRegistrationRecipient>.Instance);

    private static ReplicaRegistrationMessage Message() => new() { Owner = "Fleet", QueuedAtUtc = DateTime.UtcNow };

    private static Task<HttpResponseMessage> Approved(HttpRequestMessage _) => Task.FromResult(ReplicationFixtures.Json(HttpStatusCode.OK, new ReplicaRegistrationResult
    {
        Items = [new ReplicaRegistrationItemResult { TargetCollection = "ReplicaCars", Status = ReplicaRegistrationStatus.Approved, ApprovedHash = ReplicaCar.ReplicationHash }],
    }));

    [Fact]
    public async Task The_post_lists_the_generated_constants_and_the_answer_is_stored()
    {
        await Recipient(Approved, "Fleet").HandleAsync(Message());

        var posted = await _requests.Single().Content!.ReadFromJsonAsync<ReplicaRegistrationRequest>();
        _requests.Single().RequestUri!.ToString().Should().Be("https://fleet.test/spark/replicas/register");
        posted!.RequestingModule.Should().Be("HR");
        posted.Items.Single().Should().BeEquivalentTo(ReplicationFixtures.Item());

        using var session = Store.OpenAsyncSession();
        var state = await session.LoadAsync<ReplicaRegistrationState>(ReplicaRegistrationState.For("Fleet"));
        state!.IsSettled(ReplicationFixtures.Catalog().SetHash("Fleet")).Should().BeTrue();
    }

    /// <summary>WD15 red→green: an acknowledged, approved, unchanged set is not posted again (today every start posted).</summary>
    [Fact]
    public async Task A_settled_set_hash_posts_nothing()
    {
        await Recipient(Approved, "Fleet").HandleAsync(Message());
        _requests.Clear();

        await Recipient(Approved, "Fleet").HandleAsync(Message());

        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_owner_not_registered_yet_is_retried()
    {
        var act = () => Recipient(Approved).HandleAsync(Message());

        await act.Should().ThrowAsync<RetryAfterException>();
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_5xx_is_retried()
    {
        var act = () => Recipient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), "Fleet").HandleAsync(Message());
        await act.Should().ThrowAsync<RetryAfterException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_settled_refusal_is_not_retried(HttpStatusCode status)
    {
        var act = () => Recipient(_ => Task.FromResult(ReplicationFixtures.Json(status, new ReplicaRegistrationResult { Error = "no" })), "Fleet").HandleAsync(Message());
        await act.Should().ThrowAsync<NonRetryableException>();
    }
}

/// <summary>
/// Owner, HTTP transport (warehouse §4.2.3): the push of the current, projected state to the consumer's registry URL,
/// and its retry/dead-letter answers.
/// </summary>
public class ReplicaPushTests : SparkTestDriver
{
    private const string Id = "ReplSourceCars/1-A";
    private readonly List<HttpRequestMessage> _requests = [];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new ReplSourceCar { LicensePlate = "1-ABC", Model = "Golf", Owner = new ReplOwner { Name = "Ann" }, Mileage = 7 }, Id);
        await session.StoreAsync(new SparkReplicationSubscription
        {
            Id = SparkReplicationSubscription.For("HR", "ReplicaCars"),
            ConsumerModule = "HR",
            SourceCollection = "ReplSourceCars",
            TargetCollection = "ReplicaCars",
            ApprovedManifest = ReplicaCar.ReplicationManifest,
            ApprovedHash = ReplicaCar.ReplicationHash,
            Deployed = new ReplicationDeployment { Transport = ReplicationTransport.Http, Hash = ReplicaCar.ReplicationHash },
        }, SparkReplicationSubscription.For("HR", "ReplicaCars"));
        await session.SaveChangesAsync();
    }

    private ReplicaPushRecipient Recipient(Func<HttpRequestMessage, Task<HttpResponseMessage>> consumer, TimeProvider? clock = null)
        => new(Store, ReplicationFixtures.Directory("HR"), ReplicationFixtures.Http(consumer, _requests),
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Fleet")), clock ?? TimeProvider.System, NullLogger<ReplicaPushRecipient>.Instance);

    private static ReplicaPushMessage Message(params string[] ids) => new()
    {
        Consumer = "HR",
        TargetCollection = "ReplicaCars",
        Ids = [.. ids],
        GiveUpAtUtc = DateTime.UtcNow.AddHours(48),
    };

    private static Task<HttpResponseMessage> Ok(HttpRequestMessage _) => Task.FromResult(ReplicationFixtures.Json(HttpStatusCode.OK, new ReplicaApplyResult()));

    [Fact]
    public async Task The_push_carries_the_projection_the_hash_and_the_source_change_vector()
    {
        await Recipient(Ok).HandleAsync(Message(Id));

        _requests.Single().RequestUri!.ToString().Should().Be("https://hr.test/spark/replicas/apply");
        var body = await _requests.Single().Content!.ReadFromJsonAsync<ReplicaApplyRequest>();
        body!.SourceModule.Should().Be("Fleet");
        body.Hash.Should().Be(ReplicaCar.ReplicationHash);
        var item = body.Items.Single();
        item.SourceChangeVector.Should().NotBeNullOrEmpty();
        item.Payload!.Value.GetRawText().Should().Be("""{"LicensePlate":"1-ABC","Model":"Golf","OwnerName":"Ann"}""",
            "only the manifest's fields leave the owner (Mileage stays)");
    }

    [Fact]
    public async Task A_gone_document_is_pushed_as_a_tombstone()
    {
        await Recipient(Ok).HandleAsync(Message("ReplSourceCars/404-A"));

        var body = await _requests.Single().Content!.ReadFromJsonAsync<ReplicaApplyRequest>();
        body!.Items.Single().Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task An_unreachable_consumer_is_retried_on_the_ladder()
    {
        var act = () => Recipient(_ => throw new HttpRequestException("down")).HandleAsync(Message(Id));
        (await act.Should().ThrowAsync<RetryAfterException>()).Which.Delay.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Past_its_give_up_time_a_push_is_dead_lettered_for_the_reconcile()
    {
        var message = Message(Id);
        message.GiveUpAtUtc = DateTime.UtcNow.AddMinutes(-1);
        var act = () => Recipient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))).HandleAsync(message);
        await act.Should().ThrowAsync<NonRetryableException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_4xx_is_dead_lettered_at_once(HttpStatusCode status)
    {
        var act = () => Recipient(_ => Task.FromResult(new HttpResponseMessage(status))).HandleAsync(Message(Id));
        await act.Should().ThrowAsync<NonRetryableException>();
    }

    [Fact]
    public async Task A_subscription_that_no_longer_delivers_over_HTTP_drops_the_push()
    {
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<SparkReplicationSubscription>(SparkReplicationSubscription.For("HR", "ReplicaCars")))!.State = ReplicationSubscriptionStates.Suspended;
            await session.SaveChangesAsync();
        }

        await Recipient(Ok).HandleAsync(Message(Id));

        _requests.Should().BeEmpty();
    }
}

/// <summary>
/// Owner reconcile (warehouse §4.2.4): what pushes missed is repaired by one run, through the push path. Red on e0c542f9:
/// there was no reconcile; a raw write or a dead push stayed missing.
/// </summary>
public class ReplicaReconcileTests : SparkTestDriver
{
    private readonly IReplicaFanOut _fanOut = Substitute.For<IReplicaFanOut>();
    private readonly IModuleGrants _grants = Substitute.For<IModuleGrants>();
    private readonly IReplicaSubscriptions _subscriptions = Substitute.For<IReplicaSubscriptions>();

    public ReplicaReconcileTests()
        => _grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(true, [])));

    private async Task<Dictionary<string, string>> SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        foreach (var n in new[] { 1, 2, 3 })
            await session.StoreAsync(new ReplSourceCar { LicensePlate = $"P-{n}" }, $"ReplSourceCars/{n}-A");
        await session.StoreAsync(new SparkReplicationSubscription
        {
            Id = SparkReplicationSubscription.For("HR", "ReplicaCars"),
            ConsumerModule = "HR",
            SourceCollection = "ReplSourceCars",
            TargetCollection = "ReplicaCars",
            ApprovedManifest = ReplicaCar.ReplicationManifest,
            ApprovedHash = ReplicaCar.ReplicationHash,
            Deployed = new ReplicationDeployment { Transport = ReplicationTransport.Http, Hash = ReplicaCar.ReplicationHash },
        }, SparkReplicationSubscription.For("HR", "ReplicaCars"));
        await session.SaveChangesAsync();

        var vectors = new Dictionary<string, string>();
        foreach (var n in new[] { 1, 2, 3 })
            vectors[$"ReplSourceCars/{n}-A"] = session.Advanced.GetChangeVectorFor(await session.LoadAsync<ReplSourceCar>($"ReplSourceCars/{n}-A"))!;
        return vectors;
    }

    private ReplicaReconciler Reconciler(ReplicaDigestPage digest)
        => new(Store, ReplicationFixtures.Directory("HR"),
            ReplicationFixtures.Http(_ => Task.FromResult(ReplicationFixtures.Json(HttpStatusCode.OK, digest))),
            _grants, _subscriptions, _fanOut, Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Fleet")),
            NullLogger<ReplicaReconciler>.Instance);

    [Fact]
    public async Task Missing_and_older_replicas_are_pushed_and_extra_ones_get_tombstones()
    {
        var vectors = await SeedAsync();
        var digest = new ReplicaDigestPage
        {
            Items =
            [
                new ReplicaDigestEntry { Id = "ReplSourceCars/1-A", SourceChangeVector = vectors["ReplSourceCars/1-A"] },  // current
                new ReplicaDigestEntry { Id = "ReplSourceCars/2-A", SourceChangeVector = "A:0-old" },                       // older
                new ReplicaDigestEntry { Id = "ReplSourceCars/9-A", SourceChangeVector = "A:1-gone" },                      // gone here
            ],
        };

        var pushed = await Reconciler(digest).ReconcileAsync("HR", "ReplicaCars", CancellationToken.None);

        pushed.Should().Be(3);
        await _fanOut.Received(1).PublishCollectionAsync("ReplSourceCars",
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { "ReplSourceCars/2-A", "ReplSourceCars/3-A", "ReplSourceCars/9-A" })),
            Arg.Any<CancellationToken>(), "HR", "ReplicaCars");
    }

    [Fact]
    public async Task An_empty_consumer_gets_everything_the_initial_load()
    {
        await SeedAsync();

        (await Reconciler(new ReplicaDigestPage()).ReconcileAsync("HR", "ReplicaCars", CancellationToken.None)).Should().Be(3);
    }

    /// <summary>§4.2.6: a lost grant suspends at reconcile time instead of pushing.</summary>
    [Fact]
    public async Task A_lost_grant_suspends_instead_of_pushing()
    {
        await SeedAsync();
        _grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(false, [])));

        (await Reconciler(new ReplicaDigestPage()).ReconcileAsync("HR", "ReplicaCars", CancellationToken.None)).Should().Be(0);

        await _subscriptions.Received(1).SuspendAsync("HR", "ReplicaCars", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fanOut.DidNotReceiveWithAnyArgs().PublishCollectionAsync(default!, default!, default, default, default);
    }
}
