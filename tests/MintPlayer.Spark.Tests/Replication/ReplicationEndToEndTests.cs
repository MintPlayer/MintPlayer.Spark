using System.Net;
using System.Net.Http.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Newtonsoft.Json;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Owner and consumer on two databases (warehouse §4.2.3, WM1): a burst of saves ends at the latest state on the
/// consumer, whatever order its pushes are delivered in, because every push carries the state of its delivery, not of
/// its save. The owner's push recipient posts straight into the consumer's applier (the HTTP hop without the host).
/// </summary>
public class ReplicaPushBurstTests : SparkTestDriver
{
    private const string Id = "ReplSourceCars/1-A";
    private readonly string _consumerDatabase = $"ReplConsumer-{Guid.NewGuid():N}";
    private readonly List<ReplicaApplyOutcome> _outcomes = [];
    private IDocumentStore _consumer = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(_consumerDatabase)));
        _consumer = new DocumentStore { Urls = Store.Urls, Database = _consumerDatabase }.Initialize();

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new ReplSourceCar { LicensePlate = "1-ABC", Model = "v0", Owner = new ReplOwner { Name = "Ann" } }, Id);
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

    public override async Task DisposeAsync()
    {
        _consumer.Dispose();
        Store.Maintenance.Server.Send(new DeleteDatabasesOperation(_consumerDatabase, hardDelete: true));
        await base.DisposeAsync();
    }

    /// <summary>The consumer's <c>POST /spark/replicas/apply</c>, called as the authenticated module <c>Fleet</c>.</summary>
    private async Task<HttpResponseMessage> ConsumerAsync(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadFromJsonAsync<ReplicaApplyRequest>();
        var applier = new ReplicaApplier(_consumer, ReplicationFixtures.Catalog(ReplicationFixtures.Options("HR")), Substitute.For<IMessageOutbox>(),
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System, NullLogger<ReplicaApplier>.Instance);
        var (result, refusal) = await applier.ApplyAsync("Fleet", body!, CancellationToken.None);
        if (refusal is not null)
            return ReplicationFixtures.Json((HttpStatusCode)refusal.StatusCode, new { error = refusal.Error });
        lock (_outcomes)
            _outcomes.AddRange(result!.Items.Select(i => i.Outcome));
        return ReplicationFixtures.Json(HttpStatusCode.OK, result);
    }

    private ReplicaPushRecipient Owner()
        => new(Store, ReplicationFixtures.Directory("HR"), ReplicationFixtures.Http(ConsumerAsync),
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Fleet")), TimeProvider.System, NullLogger<ReplicaPushRecipient>.Instance, new ReplicaRowScopes([]));

    private static ReplicaPushMessage Message() => new()
    {
        Consumer = "HR",
        TargetCollection = "ReplicaCars",
        Ids = [Id],
        GiveUpAtUtc = DateTime.UtcNow.AddHours(48),
    };

    [Fact]
    public async Task A_burst_of_five_saves_leaves_the_replica_at_the_latest_state()
    {
        var owner = Owner();
        var queued = new List<ReplicaPushMessage>();
        for (var i = 1; i <= 5; i++)
        {
            using (var session = Store.OpenAsyncSession())
            {
                var car = await session.LoadAsync<ReplSourceCar>(Id);
                car!.Model = $"v{i}";
                await session.SaveChangesAsync();
            }
            queued.Add(Message());

            // The first push is delivered mid-burst: it carries what the owner holds then (v2), not its own save (v1).
            if (i == 2)
                await owner.HandleAsync(queued[0]);
        }

        // The rest arrive late and out of order (the queue promises neither order nor once-only delivery).
        foreach (var message in queued.Skip(1).Reverse())
            await owner.HandleAsync(message);

        string ownerVector;
        using (var session = Store.OpenAsyncSession())
            ownerVector = session.Advanced.GetChangeVectorFor(await session.LoadAsync<ReplSourceCar>(Id))!;

        using var consumer = _consumer.OpenAsyncSession();
        var replica = await consumer.LoadAsync<ReplicaCar>(Id);
        replica!.Model.Should().Be("v5");
        replica.LicensePlate.Should().Be("1-ABC");
        replica.OwnerName.Should().Be("Ann");
        consumer.Advanced.GetMetadataFor(replica)[ReplicaMetadata.SourceChangeVector]?.ToString().Should().Be(ownerVector);
        _outcomes.Count(o => o == ReplicaApplyOutcome.Applied).Should().Be(2, "v2 mid-burst, then v5 once; the other three pushes carry v5 again");
        _outcomes.Count(o => o == ReplicaApplyOutcome.Stale).Should().Be(3);
    }
}

/// <summary>
/// The capture hop on the owner (warehouse §4.2.3 "Capture"): a write's after-commit work is stored in the write's
/// own commit, and the interceptor it names turns it into one push message per HTTP consumer. Before this, the push
/// recipient was tested only from a hand-made message.
/// </summary>
public class ReplicaCaptureTests : SparkTestDriver
{
    private const string Id = "ReplSourceCars/1-A";

    private readonly IMessageBus _bus = Substitute.For<IMessageBus>();
    private readonly IReplicaSourceTypes _sourceTypes = Substitute.For<IReplicaSourceTypes>();
    private readonly IReplicaSubscriptions _subscriptions = Substitute.For<IReplicaSubscriptions>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ISecurityConfigurationLoader _security = Substitute.For<ISecurityConfigurationLoader>();

    public ReplicaCaptureTests()
    {
        _sourceTypes.CollectionOf(typeof(ReplSourceCar)).Returns("ReplSourceCars");
        _sourceTypes.CollectionOf(typeof(ReplOwner)).Returns("ReplOwners");
        _modelLoader.GetEntityTypeByClrType(Arg.Any<string>()).Returns(new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Model" });
        _security.GetConfiguration().Returns(new SecurityConfiguration { Rights = [new Right { Resource = "Replicate/ReplSourceCars" }] });
        _subscriptions.DeliveringAsync("ReplSourceCars", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SparkReplicationSubscription>>(
        [
            Subscription("HR", ReplicationTransport.Http),
            Subscription("Shop", ReplicationTransport.Http),
            Subscription("Audit", ReplicationTransport.Etl),
        ]));
    }

    private static SparkReplicationSubscription Subscription(string consumer, ReplicationTransport transport) => new()
    {
        Id = SparkReplicationSubscription.For(consumer, "ReplicaCars"),
        ConsumerModule = consumer,
        SourceCollection = "ReplSourceCars",
        TargetCollection = "ReplicaCars",
        ApprovedManifest = ReplicaCar.ReplicationManifest,
        ApprovedHash = ReplicaCar.ReplicationHash,
        Deployed = new ReplicationDeployment { Transport = transport, Hash = ReplicaCar.ReplicationHash },
    };

    private ReplicaPublishInterceptor Interceptor()
        => new(new ReplicaFanOut(_subscriptions, _sourceTypes, _bus, Store, Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Fleet")), TimeProvider.System),
            ReplicationFixtures.Catalog(), _modelLoader, _sourceTypes, _security);

    private async Task<List<SparkAfterCommitWork>> StoredWorkAsync()
    {
        using var session = Store.OpenAsyncSession();
        var messages = await session.Advanced.LoadStartingWithAsync<SparkMessage>("SparkMessages/", pageSize: 128);
        return messages
            .Where(m => m.MessageType.StartsWith(typeof(SparkAfterCommitWork).FullName + ",", StringComparison.Ordinal))
            .Select(m => JsonConvert.DeserializeObject<SparkAfterCommitWork>(m.PayloadJson)!)
            .ToList();
    }

    [Fact]
    public void Only_model_types_whose_collection_may_be_replicated_are_captured()
    {
        var interceptor = Interceptor();

        interceptor.AppliesTo(typeof(ReplSourceCar)).Should().BeTrue();
        interceptor.AppliesTo(typeof(ReplOwner)).Should().BeFalse("no security.json right grants Replicate/ReplOwners");
        interceptor.AppliesTo(typeof(ReplicaCar)).Should().BeFalse("a replica is never re-published");
    }

    /// <summary>
    /// The hop end to end: the write and its after-commit work commit together (a write that never commits pushes
    /// nothing), and the work, as messaging stores it, fans out one push per HTTP consumer (ETL consumers get the
    /// change from their RavenDB task).
    /// </summary>
    [Fact]
    public async Task An_owner_write_commits_its_capture_and_the_capture_queues_one_push_per_HTTP_consumer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var publisher = new ReplicaPublisher(scope.ServiceProvider.GetRequiredService<ISparkAfterCommitOutbox>(), TimeProvider.System);

        using (var abandoned = Store.OpenAsyncSession())
        {
            await abandoned.StoreAsync(new ReplSourceCar { LicensePlate = "1-ABC" }, Id);
            await publisher.MarkChangedAsync(abandoned, typeof(ReplSourceCar), Id);
        }
        (await StoredWorkAsync()).Should().BeEmpty("nothing is captured for a write that never committed");

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new ReplSourceCar { LicensePlate = "1-ABC" }, Id);
            await publisher.MarkChangedAsync(session, typeof(ReplSourceCar), Id);
            await session.SaveChangesAsync();
        }

        var work = (await StoredWorkAsync()).Should().ContainSingle().Which;
        work.InterceptorType.Should().Be(typeof(ReplicaPublishInterceptor).FullName);
        work.IsDelete.Should().BeFalse();
        work.Change.Id.Should().Be(Id);
        work.Change.EntityType.Should().Be(typeof(ReplSourceCar).FullName);

        await Interceptor().OnAfterSaveCommittedAsync(work.Change, CancellationToken.None);

        foreach (var consumer in new[] { "HR", "Shop" })
        {
            await _bus.Received(1).BroadcastAsync(
                Arg.Is<ReplicaPushMessage>(m => m.Consumer == consumer && m.TargetCollection == "ReplicaCars" && m.Ids.SequenceEqual(new[] { Id })),
                Arg.Any<BroadcastOptions>(), Arg.Any<CancellationToken>());
        }
        await _bus.DidNotReceive().BroadcastAsync(Arg.Is<ReplicaPushMessage>(m => m.Consumer == "Audit"), Arg.Any<BroadcastOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_committed_delete_is_pushed_too()
    {
        await Interceptor().OnAfterDeleteCommittedAsync(new SparkCommittedChange
        {
            EntityType = typeof(ReplSourceCar).FullName!,
            Id = Id,
            Operation = PersistentObjectOperation.Delete,
            OccurredAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await _bus.Received(2).BroadcastAsync(Arg.Is<ReplicaPushMessage>(m => m.Ids.SequenceEqual(new[] { Id })), Arg.Any<BroadcastOptions>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// The point of the redesign (warehouse §4.2, WD17): replication rides messaging's one RavenDB subscription and opens
/// none of its own. Red on e0c542f9: the SyncAction subscription worker was a second one (2 of Community's 3).
/// </summary>
public class ReplicationSubscriptionCountTests : SparkTestDriver
{
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    [Fact]
    public async Task Messaging_and_replication_together_hold_exactly_one_subscription()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        services.AddSparkReplication(o =>
        {
            o.ModuleName = "HR";
            o.ModuleUrl = "https://hr.test";
        });
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128).Any(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant),
                $"messaging to create its '{MessageFeeder.SubscriptionNameConstant}' subscription",
                TimeSpan.FromSeconds(10));

            Store.Subscriptions.GetSubscriptions(0, 128).Select(s => s.SubscriptionName)
                .Should().BeEquivalentTo([MessageFeeder.SubscriptionNameConstant]);
        }
        finally
        {
            foreach (var service in Enumerable.Reverse(hosted))
                await service.StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>
/// The deploy recipient's skip (WD15) compares transport, destination, hash and scope, so a consumer whose registry
/// entry moves to another database server is redeployed there, and only then (F19: the destination is the registry's).
/// </summary>
public class ReplicaDeployRegistryTests : SparkTestDriver
{
    private const string OldUrl = "http://localhost:8080";
    private const string NewUrl = "http://raven-b.test:8080";

    private readonly IEtlTaskManager _etl = Substitute.For<IEtlTaskManager>();
    private readonly List<EtlDeployment> _deployments = [];

    public ReplicaDeployRegistryTests()
        => _etl.DeployAsync(Arg.Do<EtlDeployment>(_deployments.Add), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new EtlTaskResult(true, Updated: 1)));

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new SparkReplicationSubscription
        {
            Id = SparkReplicationSubscription.For("HR", "ReplicaCars"),
            ConsumerModule = "HR",
            SourceCollection = "ReplSourceCars",
            TargetCollection = "ReplicaCars",
            ApprovedManifest = ReplicaCar.ReplicationManifest,
            ApprovedHash = ReplicaCar.ReplicationHash,
            Deployed = new ReplicationDeployment { Transport = ReplicationTransport.Etl, Hash = ReplicaCar.ReplicationHash, Destination = $"HR-db@{OldUrl}" },
        }, SparkReplicationSubscription.For("HR", "ReplicaCars"));
        await session.SaveChangesAsync();
    }

    private static IModuleDirectory Registry(string databaseUrl)
    {
        var directory = Substitute.For<IModuleDirectory>();
        directory.FindAsync("HR", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ModuleInformation?>(new ModuleInformation
        {
            AppName = "HR",
            AppUrl = "https://hr.test",
            DatabaseName = "HR-db",
            DatabaseUrls = [databaseUrl],
        }));
        return directory;
    }

    private ReplicaDeployRecipient Recipient(string databaseUrl)
    {
        var transport = Substitute.For<IReplicationTransportResolver>();
        transport.ResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(ReplicationTransport.Etl));
        var grants = Substitute.For<IModuleGrants>();
        grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(true, [])));
        return new ReplicaDeployRecipient(Store, transport, _etl, Registry(databaseUrl), grants, Substitute.For<IMessageOutbox>(),
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Fleet")), TimeProvider.System, NullLogger<ReplicaDeployRecipient>.Instance, new ReplicaRowScopes([]));
    }

    private async Task<string?> DeployedDestinationAsync()
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<SparkReplicationSubscription>(SparkReplicationSubscription.For("HR", "ReplicaCars")))!.Deployed?.Destination;
    }

    [Fact]
    public async Task An_unchanged_registry_entry_redeploys_nothing()
    {
        await Recipient(OldUrl).HandleAsync(new ReplicaDeployMessage { Consumer = "HR" });

        _deployments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_changed_registry_url_redeploys_to_the_new_server_once()
    {
        await Recipient(NewUrl).HandleAsync(new ReplicaDeployMessage { Consumer = "HR" });

        var deployment = _deployments.Should().ContainSingle().Which;
        deployment.TargetUrls.Should().BeEquivalentTo([NewUrl]);
        deployment.TargetDatabase.Should().Be("HR-db");
        (await DeployedDestinationAsync()).Should().Be($"HR-db@{NewUrl}");

        await Recipient(NewUrl).HandleAsync(new ReplicaDeployMessage { Consumer = "HR" });
        _deployments.Count.Should().Be(1, "the new destination is recorded, so the next deploy is unchanged");
    }
}

/// <summary>
/// A replica class that names no <c>SourceModule</c> (a generic library's) takes its owners from
/// <c>Spark:Replication:Sources:{TargetCollection}</c> (warehouse §4.2.1, owner decision 2026-10-20): bound from
/// configuration, accepted from those modules only, and refused at startup when nothing binds it.
/// </summary>
public class ReplicaSourcesConfigurationTests : SparkTestDriver
{
    private static SparkReplicationOptions Bound(Dictionary<string, string?> settings)
    {
        settings["Spark:Replication:ModuleName"] = "HR";
        settings["Spark:Replication:ModuleUrl"] = "https://hr.test";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var builder = new SparkBuilder(new ServiceCollection(), configuration);
        builder.AddReplication(_ => { });
        return builder.Services.BuildServiceProvider().GetRequiredService<IOptions<SparkReplicationOptions>>().Value;
    }

    private ReplicaApplier Applier(ReplicaCatalog catalog)
        => new(Store, catalog, Substitute.For<IMessageOutbox>(), new ServiceCollection().BuildServiceProvider(), TimeProvider.System, NullLogger<ReplicaApplier>.Instance);

    private static ReplicaApplyRequest Push(string module) => new()
    {
        SourceModule = module,
        TargetCollection = ReplicaTruck.ReplicationTargetCollection,
        Hash = ReplicaTruck.ReplicationHash,
        Items =
        [
            new ReplicaItem
            {
                Id = "ReplSourceCars/7-A",
                SourceChangeVector = "A:1-dbFleet",
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { LicensePlate = "7-TRK", Model = "Actros" }),
            },
        ],
    };

    [Fact]
    public async Task The_configured_module_is_the_source_and_its_pushes_are_applied()
    {
        var catalog = new ReplicaCatalog(Bound(new() { ["Spark:Replication:Sources:ReplicaTrucks:0"] = "Fleet" }), typeof(ReplicaTruck));

        catalog.For(typeof(ReplicaTruck))!.Sources.Should().BeEquivalentTo(["Fleet"]);
        catalog.Owners.Should().BeEquivalentTo(["Fleet"]);
        catalog.Problems(Store).Should().BeEmpty();

        var (result, refusal) = await Applier(catalog).ApplyAsync("Fleet", Push("Fleet"), CancellationToken.None);
        refusal.Should().BeNull();
        result!.Items.Single().Outcome.Should().Be(ReplicaApplyOutcome.Applied);
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<ReplicaTruck>("ReplSourceCars/7-A"))!.Model.Should().Be("Actros");
    }

    [Fact]
    public async Task A_module_the_configuration_does_not_name_is_refused()
    {
        var catalog = new ReplicaCatalog(Bound(new() { ["Spark:Replication:Sources:ReplicaTrucks:0"] = "Fleet" }), typeof(ReplicaTruck));

        var (_, refusal) = await Applier(catalog).ApplyAsync("Shop", Push("Shop"), CancellationToken.None);

        refusal!.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Several_configured_owners_all_feed_the_replica()
    {
        var catalog = new ReplicaCatalog(Bound(new()
        {
            ["Spark:Replication:Sources:ReplicaTrucks:0"] = "ShopA",
            ["Spark:Replication:Sources:ReplicaTrucks:1"] = "ShopB",
        }), typeof(ReplicaTruck));

        catalog.FromOwner("ShopA").Select(d => d.Type).Should().BeEquivalentTo([typeof(ReplicaTruck)]);
        catalog.FromOwner("ShopB").Select(d => d.Type).Should().BeEquivalentTo([typeof(ReplicaTruck)]);
    }

    [Fact]
    public void Without_a_binding_startup_names_the_missing_source()
    {
        var catalog = new ReplicaCatalog(Bound(new()), typeof(ReplicaTruck));

        catalog.Problems(Store).Should().ContainSingle().Which.Should().Contain("Spark:Replication:Sources:ReplicaTrucks");
    }

    [Fact]
    public void A_declared_SourceModule_wins_over_the_configuration()
    {
        var catalog = new ReplicaCatalog(Bound(new() { ["Spark:Replication:Sources:ReplicaCars:0"] = "Other" }), typeof(ReplicaCar));

        catalog.For(typeof(ReplicaCar))!.Sources.Should().BeEquivalentTo(["Fleet"]);
    }
}

/// <summary>
/// A replica class with the constants <c>ReplicatedGenerator</c> emits for
/// <c>[Replicated(SourceCollection = "ReplSourceCars")]</c>: no <c>SourceModule</c>, so its owners come from
/// <c>Spark:Replication:Sources:ReplicaTrucks</c>. Hash = <c>ReplicaScriptRenderer.Hash(manifest, script)</c>.
/// </summary>
[Replicated(SourceCollection = "ReplSourceCars")]
public class ReplicaTruck : IReplicatedEntity
{
    public const string ReplicationSourceCollection = "ReplSourceCars";
    public const string ReplicationTargetCollection = "ReplicaTrucks";
    public const string? ReplicationSourceModule = null;
    public const bool ReplicationWriteBack = false;
    public const string ReplicationManifest = "{\"v\":1,\"source\":\"ReplSourceCars\",\"target\":\"ReplicaTrucks\",\"fields\":[[\"LicensePlate\",\"LicensePlate\"],[\"Model\",\"Model\"]]}";
    public const string ReplicationScript = "loadToReplicaTrucks({\n    LicensePlate: this.LicensePlate,\n    Model: this.Model,\n    '@metadata': { 'Spark-Replica-Source-Change-Vector': getMetadata(this)['@change-vector'] }\n});";
    public const string ReplicationHash = "sha256:3504948175221b5a675b4d0ec6b6820d49be2fe836c0778cd6141e1ec496cbb6";

    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
