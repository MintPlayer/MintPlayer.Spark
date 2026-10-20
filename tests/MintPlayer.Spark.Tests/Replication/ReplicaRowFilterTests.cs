using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Licensing;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>An owner document that carries the owner of the goods, like every warehouse-owned fulfilment contract (collection <c>ReplOwnedRows</c>).</summary>
public class ReplOwnedRow
{
    public string? Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>A test <see cref="IReplicaRowFilter"/>: consumer module → the owner ids it may receive, on <c>OwnerId</c>.</summary>
internal sealed class OwnerIdRowFilter(Dictionary<string, string[]> owners, string collection = ReplicaRowFilterFixtures.Collection) : IReplicaRowFilter
{
    public Dictionary<string, string[]> Owners { get; } = owners;

    public Task<ReplicaRowCondition?> GetConditionAsync(string consumerModule, string sourceCollection, CancellationToken cancellationToken)
        => Task.FromResult(sourceCollection != collection
            ? null
            : new ReplicaRowCondition("OwnerId", Owners.TryGetValue(consumerModule, out var ids) ? ids : []));
}

internal static class ReplicaRowFilterFixtures
{
    public const string Collection = "ReplOwnedRows";
    public const string RowA = "ReplOwnedRows/a-1";
    public const string RowB = "ReplOwnedRows/b-1";

    public static readonly ReplicaManifest Manifest = new(Collection, Collection, [new ReplicaField("OwnerId", ["OwnerId"]), new ReplicaField("Name", ["Name"])]);
    public static readonly string ManifestJson = ReplicaScriptRenderer.ToJson(Manifest);
    public static readonly string Hash = ReplicaScriptRenderer.Hash(ManifestJson, ReplicaScriptRenderer.RenderScript(Manifest));

    /// <summary>The warehouse's situation: one owner module serving two shops; ShopB's link names owner "B" only.</summary>
    public static OwnerIdRowFilter Filter() => new(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["ShopA"] = ["A"], ["ShopB"] = ["B"] });

    public static SparkReplicationSubscription Subscription(string consumer, ReplicationTransport transport, string? scope = null) => new()
    {
        Id = SparkReplicationSubscription.For(consumer, Collection),
        ConsumerModule = consumer,
        SourceCollection = Collection,
        TargetCollection = Collection,
        ApprovedManifest = ManifestJson,
        ApprovedHash = Hash,
        Deployed = new ReplicationDeployment { Transport = transport, Hash = Hash, Scope = scope },
    };

    public static async Task SeedAsync(IDocumentStore store, params SparkReplicationSubscription[] subscriptions)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new ReplOwnedRow { OwnerId = "A", Name = "shop A's parcel" }, RowA);
        await session.StoreAsync(new ReplOwnedRow { OwnerId = "B", Name = "shop B's parcel" }, RowB);
        foreach (var subscription in subscriptions)
            await session.StoreAsync(subscription, subscription.Id);
        await session.SaveChangesAsync();
    }
}

/// <summary>
/// The per-consumer row filter (warehouse §4.2.3 "Row filter", Q-W23), HTTP push: a warehouse serving two shops sends
/// shop B only shop B's rows. Red before the filter: the push loaded and projected every id it was given, so shop A's
/// row (content and id) reached shop B.
/// </summary>
public class ReplicaRowFilterPushTests : SparkTestDriver
{
    private readonly List<HttpRequestMessage> _requests = [];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await ReplicaRowFilterFixtures.SeedAsync(Store, ReplicaRowFilterFixtures.Subscription("ShopB", ReplicationTransport.Http));
    }

    private ReplicaPushRecipient Recipient(params IReplicaRowFilter[] filters)
        => new(Store, ReplicationFixtures.Directory("ShopB"),
            ReplicationFixtures.Http(_ => Task.FromResult(ReplicationFixtures.Json(HttpStatusCode.OK, new ReplicaApplyResult())), _requests),
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Warehouse")), TimeProvider.System,
            NullLogger<ReplicaPushRecipient>.Instance, new ReplicaRowScopes(filters));

    private static ReplicaPushMessage Message(string[] ids, string[]? held = null) => new()
    {
        Consumer = "ShopB",
        TargetCollection = ReplicaRowFilterFixtures.Collection,
        Ids = [.. ids],
        HeldIds = [.. held ?? []],
        GiveUpAtUtc = DateTime.UtcNow.AddHours(48),
    };

    private async Task<string> PostedBodyAsync() => await _requests.Single().Content!.ReadAsStringAsync();

    [Fact]
    public async Task Shop_B_never_receives_shop_As_row_over_a_push()
    {
        await Recipient(ReplicaRowFilterFixtures.Filter()).HandleAsync(Message([ReplicaRowFilterFixtures.RowA, ReplicaRowFilterFixtures.RowB]));

        var body = await _requests.Single().Content!.ReadFromJsonAsync<ReplicaApplyRequest>();
        body!.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { ReplicaRowFilterFixtures.RowB });
        var raw = await PostedBodyAsync();
        raw.Contains("shop A").Should().BeFalse("shop A's content never leaves the owner for shop B");
        raw.Contains(ReplicaRowFilterFixtures.RowA, StringComparison.OrdinalIgnoreCase).Should().BeFalse("not even its id");
    }

    [Fact]
    public async Task A_push_of_only_out_of_scope_rows_posts_nothing()
    {
        await Recipient(ReplicaRowFilterFixtures.Filter()).HandleAsync(Message([ReplicaRowFilterFixtures.RowA]));

        _requests.Should().BeEmpty();
    }

    /// <summary>A delete under a scope cannot be checked any more: it is delivered by the reconcile, never to a consumer that does not hold it.</summary>
    [Fact]
    public async Task A_gone_row_is_not_tombstoned_to_a_scoped_consumer_that_does_not_hold_it()
    {
        await Recipient(ReplicaRowFilterFixtures.Filter()).HandleAsync(Message(["ReplOwnedRows/a-404"]));

        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_the_consumer_holds_outside_its_scope_is_tombstoned()
    {
        await Recipient(ReplicaRowFilterFixtures.Filter()).HandleAsync(Message([ReplicaRowFilterFixtures.RowA], held: [ReplicaRowFilterFixtures.RowA]));

        var body = await _requests.Single().Content!.ReadFromJsonAsync<ReplicaApplyRequest>();
        var item = body!.Items.Single();
        item.Id.Should().Be(ReplicaRowFilterFixtures.RowA);
        item.Deleted.Should().BeTrue();
        item.Payload.HasValue.Should().BeFalse("a tombstone carries no content");
    }

    [Fact]
    public async Task An_unlinked_consumer_gets_nothing_of_a_filtered_collection()
    {
        var filter = ReplicaRowFilterFixtures.Filter();
        filter.Owners.Remove("ShopB");

        await Recipient(filter).HandleAsync(Message([ReplicaRowFilterFixtures.RowA, ReplicaRowFilterFixtures.RowB]));

        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_filter_every_row_is_pushed_as_before()
    {
        await Recipient().HandleAsync(Message([ReplicaRowFilterFixtures.RowA, ReplicaRowFilterFixtures.RowB]));

        var body = await _requests.Single().Content!.ReadFromJsonAsync<ReplicaApplyRequest>();
        body!.Items.Count.Should().Be(2);
    }
}

/// <summary>
/// The row filter in the owner's reconcile (§4.2.4): only the consumer's rows are compared and pushed; rows it holds
/// outside its scope get tombstones. Red before the filter: the reconcile pushed every source row to every consumer.
/// </summary>
public class ReplicaRowFilterReconcileTests : SparkTestDriver
{
    private readonly IReplicaFanOut _fanOut = Substitute.For<IReplicaFanOut>();
    private readonly IModuleGrants _grants = Substitute.For<IModuleGrants>();

    public ReplicaRowFilterReconcileTests()
        => _grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(true, [])));

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await ReplicaRowFilterFixtures.SeedAsync(Store, ReplicaRowFilterFixtures.Subscription("ShopB", ReplicationTransport.Http));
    }

    private ReplicaReconciler Reconciler(ReplicaDigestPage digest)
        => new(Store, ReplicationFixtures.Directory("ShopB"),
            ReplicationFixtures.Http(_ => Task.FromResult(ReplicationFixtures.Json(HttpStatusCode.OK, digest))),
            _grants, Substitute.For<IReplicaSubscriptions>(), _fanOut, Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Warehouse")),
            NullLogger<ReplicaReconciler>.Instance, new ReplicaRowScopes([ReplicaRowFilterFixtures.Filter()]));

    [Fact]
    public async Task Shop_B_never_receives_shop_As_row_over_the_reconcile()
    {
        var pushed = await Reconciler(new ReplicaDigestPage()).ReconcileAsync("ShopB", ReplicaRowFilterFixtures.Collection, CancellationToken.None);

        pushed.Should().Be(1);
        await _fanOut.Received(1).PublishCollectionAsync(ReplicaRowFilterFixtures.Collection,
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { ReplicaRowFilterFixtures.RowB })),
            Arg.Any<CancellationToken>(), "ShopB", ReplicaRowFilterFixtures.Collection, Arg.Any<IReadOnlyCollection<string>?>());
    }

    /// <summary>A consumer that received shop A's row before its scope existed (or before a link was removed) loses it.</summary>
    [Fact]
    public async Task A_row_the_consumer_holds_outside_its_scope_is_pushed_as_held_so_it_becomes_a_tombstone()
    {
        var digest = new ReplicaDigestPage { Items = [new ReplicaDigestEntry { Id = ReplicaRowFilterFixtures.RowA, SourceChangeVector = "A:1-old" }] };

        await Reconciler(digest).ReconcileAsync("ShopB", ReplicaRowFilterFixtures.Collection, CancellationToken.None);

        await _fanOut.Received(1).PublishCollectionAsync(ReplicaRowFilterFixtures.Collection,
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { ReplicaRowFilterFixtures.RowA, ReplicaRowFilterFixtures.RowB })),
            Arg.Any<CancellationToken>(), "ShopB", ReplicaRowFilterFixtures.Collection,
            Arg.Is<IReadOnlyCollection<string>?>(held => held != null && held.SequenceEqual(new[] { ReplicaRowFilterFixtures.RowA })));
    }

    /// <summary>The hop from the reconcile to the push: only held ids travel as <see cref="ReplicaPushMessage.HeldIds"/>.</summary>
    [Fact]
    public async Task The_fan_out_carries_only_the_held_ids_of_each_batch()
    {
        var subscriptions = Substitute.For<IReplicaSubscriptions>();
        subscriptions.DeliveringAsync(ReplicaRowFilterFixtures.Collection, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SparkReplicationSubscription>>([ReplicaRowFilterFixtures.Subscription("ShopB", ReplicationTransport.Http)]));
        var bus = Substitute.For<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>();
        var fanOut = new ReplicaFanOut(subscriptions, Substitute.For<IReplicaSourceTypes>(), bus, Store,
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Warehouse")), TimeProvider.System);

        await fanOut.PublishCollectionAsync(ReplicaRowFilterFixtures.Collection, [ReplicaRowFilterFixtures.RowA, ReplicaRowFilterFixtures.RowB], CancellationToken.None,
            "ShopB", ReplicaRowFilterFixtures.Collection, heldIds: [ReplicaRowFilterFixtures.RowA]);

        await bus.Received(1).BroadcastAsync(
            Arg.Is<ReplicaPushMessage>(m => m.HeldIds.SequenceEqual(new[] { ReplicaRowFilterFixtures.RowA }) && m.Ids.Count == 2),
            Arg.Any<MintPlayer.Spark.Messaging.Abstractions.BroadcastOptions>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// The row filter on the ETL transport: the owner renders its own scope into the script it deploys (the consumer posts
/// a module-independent, unfiltered script), and a changed scope re-renders and resets the transform. Red before the
/// filter: the deployed transform loaded every row of the collection for every consumer.
/// </summary>
public class ReplicaRowFilterEtlTests : SparkTestDriver
{
    private readonly IEtlTaskManager _etl = Substitute.For<IEtlTaskManager>();
    private readonly List<EtlDeployment> _deployments = [];

    public ReplicaRowFilterEtlTests()
        => _etl.DeployAsync(Arg.Do<EtlDeployment>(_deployments.Add), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new EtlTaskResult(true, Updated: 1)));

    private ReplicaDeployRecipient Recipient(OwnerIdRowFilter filter)
    {
        var transport = Substitute.For<IReplicationTransportResolver>();
        transport.ResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(ReplicationTransport.Etl));
        var grants = Substitute.For<IModuleGrants>();
        grants.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult(new ReplicaGrant(true, [])));
        return new ReplicaDeployRecipient(Store, transport, _etl, ReplicationFixtures.Directory("ShopB"), grants, Substitute.For<IMessageOutbox>(),
            Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("Warehouse")), TimeProvider.System,
            NullLogger<ReplicaDeployRecipient>.Instance, new ReplicaRowScopes([filter]));
    }

    [Fact]
    public async Task Shop_Bs_ETL_script_loads_only_shop_Bs_rows()
    {
        await ReplicaRowFilterFixtures.SeedAsync(Store, ReplicaRowFilterFixtures.Subscription("ShopB", ReplicationTransport.Etl, scope: "stale"));

        await Recipient(ReplicaRowFilterFixtures.Filter()).HandleAsync(new ReplicaDeployMessage { Consumer = "ShopB" });

        var script = _deployments.Single().Transforms.Single().Script;
        script.Should().StartWith("if ([\"B\"].indexOf(this.OwnerId) >= 0) {\n");
        script.Contains("\"A\"").Should().BeFalse("shop A's owner id is no value of shop B's scope");
        script.Should().EndWith(ReplicaScriptRenderer.RenderEtlScript(ReplicaRowFilterFixtures.Manifest, "Warehouse") + "\n}");
        string.Join(",", _deployments.Single().ResetTransforms).Should().Be(ReplicaDeployRecipient.TransformName(ReplicaRowFilterFixtures.Collection),
            "the scope differs from the deployed one, so RavenDB reprocesses the collection");
    }

    [Fact]
    public async Task An_unchanged_scope_redeploys_nothing_and_a_changed_one_redeploys()
    {
        await ReplicaRowFilterFixtures.SeedAsync(Store, ReplicaRowFilterFixtures.Subscription("ShopB", ReplicationTransport.Etl, scope: "stale"));
        var filter = ReplicaRowFilterFixtures.Filter();
        await Recipient(filter).HandleAsync(new ReplicaDeployMessage { Consumer = "ShopB" });

        await Recipient(filter).HandleAsync(new ReplicaDeployMessage { Consumer = "ShopB" });
        _deployments.Count.Should().Be(1, "same transport, destination, hash and scope");

        filter.Owners["ShopB"] = ["B", "C"];
        await Recipient(filter).HandleAsync(new ReplicaDeployMessage { Consumer = "ShopB" });
        _deployments.Count.Should().Be(2);
        _deployments[1].Transforms.Single().Script.Should().StartWith("if ([\"B\", \"C\"].indexOf(this.OwnerId) >= 0) {\n");
    }

    /// <summary>Scope values are the owner's data, still never trusted as script: every value is a \u-escaped literal.</summary>
    [Fact]
    public void A_scope_value_cannot_break_out_of_its_literal()
    {
        var scope = new ReplicaRowScope([new ReplicaRowCondition("OwnerId", ["x\"]); evil(); //'" + (char)0x2028])]);

        var script = ReplicaScriptRenderer.RenderEtlScript(ReplicaRowFilterFixtures.Manifest, "Warehouse", scope);

        script.Should().StartWith("if ([\"x\\u0022\\u005d\\u0029\\u003b evil\\u0028\\u0029\\u003b //\\u0027\\u2028\"].indexOf(this.OwnerId) >= 0) {\n");
    }

    [Fact]
    public void A_row_filter_field_must_be_an_identifier()
    {
        var act = () => new ReplicaRowCondition("Owner.Id) || (true", ["B"]);
        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// Developer licence (W6): the scoped script deployed as a real RavenDB ETL task loads only the consumer's rows into its
/// database. Under a licence without ETL the deployment refuses and there is nothing to check.
/// </summary>
public class ReplicaRowFilterEtlLoadTests : SparkTestDriver
{
    private readonly string _shopBDatabase = $"ShopB-{Guid.NewGuid():N}";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(_shopBDatabase)));
    }

    public override async Task DisposeAsync()
    {
        Store.Maintenance.Server.Send(new DeleteDatabasesOperation(_shopBDatabase, hardDelete: true));
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Shop_Bs_database_receives_only_shop_Bs_rows_from_the_ETL()
    {
        var scope = new ReplicaRowScope([new ReplicaRowCondition("OwnerId", ["B"])]);
        var script = ReplicaScriptRenderer.RenderEtlScript(ReplicaRowFilterFixtures.Manifest, "Warehouse", scope);
        var manager = new EtlTaskManager(Store, NullLogger<EtlTaskManager>.Instance, new SparkLicenseCapabilities(Store, NullLogger<SparkLicenseCapabilities>.Instance));
        var result = await manager.DeployAsync(new EtlDeployment("ShopB", _shopBDatabase, Store.Urls,
            [new EtlTransformScript(ReplicaDeployRecipient.TransformName(ReplicaRowFilterFixtures.Collection), ReplicaRowFilterFixtures.Collection, script)]));
        if (!result.Success && result.Error == EtlTaskResult.NotLicensedError)
            return;
        result.Success.Should().BeTrue(result.Error ?? "");

        await ReplicaRowFilterFixtures.SeedAsync(Store);

        using var shopB = new DocumentStore { Urls = Store.Urls, Database = _shopBDatabase }.Initialize();
        await AsyncWait.UntilAsync(() =>
        {
            using var session = shopB.OpenSession();
            return session.Load<ReplOwnedRow>(ReplicaRowFilterFixtures.RowB) is not null;
        }, "the ETL task to load shop B's row");

        // Shop B's row was loaded after shop A's (same batch or later, in etag order): A's absence is final, not "not yet".
        using var check = shopB.OpenSession();
        check.Load<ReplOwnedRow>(ReplicaRowFilterFixtures.RowA).Should().BeNull("shop A's row never reaches shop B's database");
        check.Query<ReplOwnedRow>().Customize(c => c.WaitForNonStaleResults()).Count().Should().Be(1);
    }
}
