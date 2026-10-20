using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Owner: brings one consumer's delivery in line with its subscriptions (warehouse §4.2.2 "Deploy"). On ETL, one task
/// per consumer with one transform per delivering subscription, rendered by this module from the approved manifest
/// and the owner's row scope for that consumer (§4.2.3 "Row filter"), to the destination in the consumer's registry
/// entry (F19); skipped when transport, destination, hashes and scopes are unchanged (WD15), so a changed registry
/// entry redeploys and a changed scope re-renders and resets its transform. On HTTP, a newly approved subscription,
/// or one whose scope changed, gets a reconcile. Grants are checked again here; a lost one suspends (§4.2.6).
/// </summary>
internal sealed partial class ReplicaDeployRecipient : IRecipient<ReplicaDeployMessage>
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IReplicationTransportResolver transportResolver;
    [Inject] private readonly IEtlTaskManager etlTaskManager;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IModuleGrants grants;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaDeployRecipient> logger;
    [Inject] private readonly IReplicaRowScopes rowScopes;

    private sealed record Delivery(SparkReplicationSubscription Subscription, ReplicaManifest Manifest, ReplicaRowScope? Scope)
    {
        public string? ScopeFingerprint => Scope?.Fingerprint;
    }

    public async Task HandleAsync(ReplicaDeployMessage message, CancellationToken cancellationToken = default)
    {
        var consumer = message.Consumer;
        var transport = await transportResolver.ResolveAsync(cancellationToken);

        using var session = documentStore.OpenAsyncSession();
        var all = (await session.Advanced.LoadStartingWithAsync<SparkReplicationSubscription>($"SparkReplicationSubscriptions/{consumer}/", pageSize: 1024, token: cancellationToken)).ToList();

        var delivering = new List<Delivery>();
        foreach (var subscription in all.Where(s => s.IsDelivering))
        {
            if (!ReplicaScriptRenderer.TryParse(subscription.ApprovedManifest, out var manifest, out var error))
            {
                logger.LogError("Subscription {Id} holds an approved manifest that does not parse ({Error}); it is not delivered.", subscription.Id, error);
                continue;
            }
            var grant = await grants.CheckAsync(consumer, subscription.SourceCollection, manifest!, cancellationToken);
            if (!grant.Allowed)
            {
                subscription.State = ReplicationSubscriptionStates.Suspended;
                subscription.Reason = grant.Refusal;
                logger.LogWarning("Suspended the replication of '{Target}' to '{Consumer}' at deploy: {Reason}", subscription.TargetCollection, consumer, grant.Refusal);
                continue;
            }
            delivering.Add(new Delivery(subscription, manifest!, await rowScopes.GetAsync(consumer, subscription.SourceCollection, cancellationToken)));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (transport == ReplicationTransport.Etl)
            await DeployEtlAsync(consumer, all, delivering, now, cancellationToken);
        else
            await DeployHttpAsync(session, consumer, all, delivering, now, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
    }

    internal static string TransformName(string targetCollection) => $"spark-replica-{targetCollection}";

    private async Task DeployEtlAsync(string consumer, List<SparkReplicationSubscription> all,
        List<Delivery> delivering, DateTime now, CancellationToken cancellationToken)
    {
        string? destination = null;
        string[] urls = [];
        string? database = null;
        if (delivering.Count > 0)
        {
            // F19: the destination is the consumer's registry entry, never anything it sent.
            var registration = await moduleDirectory.FindAsync(consumer, cancellationToken);
            if (registration is null || string.IsNullOrEmpty(registration.DatabaseName) || registration.DatabaseUrls.Length == 0)
                throw new InvalidOperationException($"Module '{consumer}' has no database registered in SparkModules; the deploy retries once it registers.");
            database = registration.DatabaseName;
            urls = registration.DatabaseUrls;
            destination = $"{database}@{string.Join(",", urls)}";
        }

        var unchanged = all.All(s => delivering.FirstOrDefault(d => d.Subscription == s) is { } d
            ? s.Deployed is { Transport: ReplicationTransport.Etl } deployed && deployed.Hash == s.ApprovedHash && deployed.Destination == destination && deployed.Scope == d.ScopeFingerprint
            : s.Deployed is null);
        if (unchanged)
        {
            logger.LogDebug("ETL delivery to '{Consumer}' is unchanged; nothing redeployed.", consumer);
            return;
        }

        EtlTaskResult result;
        if (delivering.Count == 0)
        {
            result = await etlTaskManager.RemoveAsync(consumer, cancellationToken);
        }
        else
        {
            var owner = options.Value.ModuleName;
            var transforms = delivering
                .Select(d => new EtlTransformScript(
                    TransformName(d.Subscription.TargetCollection),
                    d.Subscription.SourceCollection,
                    ReplicaScriptRenderer.RenderEtlScript(d.Manifest, owner, d.Scope)))
                .ToList();

            // A transform already running with another scope is reset after the update, so rows that entered the scope are
            // loaded and rows that left it are removed by the reprocessing (the script loads nothing for them).
            var reset = delivering
                .Where(d => d.Subscription.Deployed is { Transport: ReplicationTransport.Etl } deployed && deployed.Scope != d.ScopeFingerprint)
                .Select(d => TransformName(d.Subscription.TargetCollection))
                .ToList();
            result = await etlTaskManager.DeployAsync(new EtlDeployment(consumer, database!, urls, transforms) { ResetTransforms = reset }, cancellationToken);
        }

        if (!result.Success)
        {
            // F18: a licence without ETL is configuration, not a transient fault.
            if (result.Error == EtlTaskResult.NotLicensedError)
                throw new NonRetryableException($"ETL delivery to '{consumer}' refused: this RavenDB licence has no ETL. Set Spark:Replication:Transport to 'Http'.");
            throw new InvalidOperationException($"ETL delivery to '{consumer}' failed ({result.Error}); retried.");
        }

        foreach (var subscription in all)
        {
            subscription.Deployed = delivering.FirstOrDefault(d => d.Subscription == subscription) is { } delivery
                ? new ReplicationDeployment { Transport = ReplicationTransport.Etl, Hash = subscription.ApprovedHash, Destination = destination, Scope = delivery.ScopeFingerprint, DeployedAtUtc = now }
                : null;
        }
        logger.LogInformation("ETL delivery to '{Consumer}': {Count} transform(s).", consumer, delivering.Count);
    }

    private async Task DeployHttpAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string consumer, List<SparkReplicationSubscription> all,
        List<Delivery> delivering, DateTime now, CancellationToken cancellationToken)
    {
        // A switch from ETL leaves a task behind: remove it, so the two transports never both write.
        if (all.Any(s => s.Deployed?.Transport == ReplicationTransport.Etl))
        {
            var removed = await etlTaskManager.RemoveAsync(consumer, cancellationToken);
            if (!removed.Success)
                throw new InvalidOperationException($"Removing the ETL task of '{consumer}' failed ({removed.Error}); retried.");
        }

        foreach (var subscription in all)
        {
            if (delivering.FirstOrDefault(d => d.Subscription == subscription) is not { } delivery)
            {
                subscription.Deployed = null;
                continue;
            }

            if (subscription.Deployed is { Transport: ReplicationTransport.Http } deployed && deployed.Hash == subscription.ApprovedHash && deployed.Scope == delivery.ScopeFingerprint)
                continue;

            // The initial load (and the load after a new shape is approved or the scope changed): the reconcile pushes what
            // is missing or older, and tombstones what the consumer holds outside its scope.
            await outbox.EnqueueAsync(session, new ReplicaReconcileMessage { Consumer = consumer, TargetCollection = subscription.TargetCollection }, cancellationToken: cancellationToken);
            subscription.Deployed = new ReplicationDeployment { Transport = ReplicationTransport.Http, Hash = subscription.ApprovedHash, Scope = delivery.ScopeFingerprint, DeployedAtUtc = now };
            logger.LogInformation("HTTP delivery of '{Target}' to '{Consumer}' active in {Hash}; reconcile queued.", subscription.TargetCollection, consumer, subscription.ApprovedHash);
        }
    }
}
