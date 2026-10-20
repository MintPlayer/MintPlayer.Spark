using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Owner, HTTP transport (warehouse §4.2.3 "Delivery"): loads the <b>current</b> documents and their change vectors (so
/// a burst of writes becomes one push of the latest state), projects them through the approved manifest, and posts them
/// to the consumer's <c>AppUrl</c> from the registry (F19) over the module certificate.
/// <list type="bullet">
/// <item>unreachable / 5xx / 429 → <see cref="RetryAfterException"/> on the ladder (1 m / 5 m / 30 m / 2 h), until the
/// push's give-up time; then it is dead-lettered and the next reconcile repairs it;</item>
/// <item>409 (manifest mismatch) and any other 4xx → <see cref="NonRetryableException"/> with the reason.</item>
/// </list>
/// </summary>
internal sealed partial class ReplicaPushRecipient : IRecipient<ReplicaPushMessage>
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IReplicationHttpClientProvider httpClients;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaPushRecipient> logger;

    public async Task HandleAsync(ReplicaPushMessage message, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var subscription = await session.LoadAsync<SparkReplicationSubscription>(
            SparkReplicationSubscription.For(message.Consumer, message.TargetCollection), cancellationToken);
        // Withdrawn, suspended or moved to ETL since the push was queued: nothing to deliver.
        if (subscription is not { IsDelivering: true, Deployed.Transport: ReplicationTransport.Http }
            || !ReplicaScriptRenderer.TryParse(subscription.ApprovedManifest, out var manifest, out _))
        {
            logger.LogDebug("Push of {Count} document(s) to '{Consumer}'/{Target} dropped: the subscription no longer delivers over HTTP.",
                message.Ids.Count, message.Consumer, message.TargetCollection);
            return;
        }

        var request = new ReplicaApplyRequest
        {
            SourceModule = options.Value.ModuleName,
            TargetCollection = message.TargetCollection,
            Hash = subscription.ApprovedHash!,
            Items = await ReplicaItems.ReadAsync(documentStore, session, manifest!, message.Ids, cancellationToken),
        };

        var registration = await moduleDirectory.FindAsync(message.Consumer, cancellationToken);
        if (registration is null || string.IsNullOrEmpty(registration.AppUrl))
        {
            Retry(message, $"module '{message.Consumer}' is not registered in SparkModules");
            return;
        }

        var result = await ModuleCalls.PostAsync<ReplicaApplyRequest, ReplicaApplyResult>(
            httpClients.GetClient(message.Consumer), registration.AppUrl, "/spark/replicas/apply", request, cancellationToken);

        if (result.IsSuccess)
        {
            foreach (var refused in result.Value?.Items.Where(i => i.Outcome == ReplicaApplyOutcome.Refused) ?? [])
                logger.LogWarning("Module '{Consumer}' refused replica {Id} of {Target}: {Reason}", message.Consumer, refused.Id, message.TargetCollection, refused.Reason);
            return;
        }

        if (result.IsTransient)
        {
            Retry(message, $"module '{message.Consumer}' answered {(result.StatusCode is { } code ? ((int)code).ToString() : "nothing")}{(result.Error is null ? "" : ": " + result.Error.Message)}");
            return;
        }

        throw new NonRetryableException($"Push of {message.TargetCollection} to '{message.Consumer}' refused with {(int)result.StatusCode!}: {result.Body}");
    }

    private void Retry(ReplicaPushMessage message, string why)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (now >= message.GiveUpAtUtc)
            throw new NonRetryableException($"Push of {message.TargetCollection} to '{message.Consumer}' gave up ({why}); the next reconcile repairs it.");

        var started = message.GiveUpAtUtc - options.Value.Push.GiveUpAfter;
        throw new RetryAfterException(RetryLadder.Next(options.Value.Push.ResolvedRetryLadder, now - started), $"Push to '{message.Consumer}' deferred: {why}");
    }
}

/// <summary>Reads source documents as replica items: the projection of each, or a tombstone for an id that is gone.</summary>
internal static class ReplicaItems
{
    public static async Task<List<ReplicaItem>> ReadAsync(IDocumentStore store, Raven.Client.Documents.Session.IAsyncDocumentSession session,
        ReplicaManifest manifest, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var loaded = await session.LoadAsync<object>(ids.Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var items = new List<ReplicaItem>(ids.Count);
        foreach (var (id, entity) in loaded)
        {
            if (entity is null)
            {
                items.Add(new ReplicaItem { Id = id, Deleted = true });
                continue;
            }

            var source = ReplicaJson.Serialize(store, entity);
            var payload = ManifestProjector.Project(manifest, source);
            items.Add(new ReplicaItem
            {
                Id = session.Advanced.GetDocumentId(entity) ?? id,
                SourceChangeVector = session.Advanced.GetChangeVectorFor(entity),
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload),
            });
        }
        return items;
    }
}
