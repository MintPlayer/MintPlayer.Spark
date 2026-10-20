using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>The owner's answer to a registration, with the HTTP status it maps to.</summary>
internal sealed record ReplicaRegisterOutcome(int StatusCode, ReplicaRegistrationResult Result);

/// <summary>How a decision on a replication request ended.</summary>
internal enum ReplicationDecisionOutcome
{
    Done,
    NotFound,
    NotPending,
    /// <summary>The consumer no longer holds the grant or a field's read right: suspended instead.</summary>
    NotGranted,
}

/// <summary>
/// The owner's book of consumers and the approval gate (warehouse §4.2.2, WD15, WD16). Only the authenticated module's
/// subscriptions change on its registration, and every destination is that module's registry entry (F19).
/// </summary>
internal interface IReplicaSubscriptions
{
    /// <summary>
    /// Validates and records <paramref name="module"/>'s registration: the re-render check (F16), the grant and attribute
    /// read rights (F17), one request per new shape (auto-approved iff this host is Development), and the withdrawal of
    /// what the post no longer lists (F18).
    /// </summary>
    Task<ReplicaRegisterOutcome> RegisterAsync(string module, IReadOnlyList<ReplicaRegistrationItem> items, CancellationToken cancellationToken);

    /// <summary>An administrator approves a pending request; the delivery is redeployed through messaging.</summary>
    Task<ReplicationDecisionOutcome> ApproveAsync(string requestId, string? approver, CancellationToken cancellationToken);

    /// <summary>An administrator rejects a pending request: the subscription is suspended and its delivery removed.</summary>
    Task<ReplicationDecisionOutcome> RejectAsync(string requestId, string? approver, string? reason, CancellationToken cancellationToken);

    /// <summary>The subscriptions that deliver from <paramref name="sourceCollection"/> now.</summary>
    Task<IReadOnlyList<SparkReplicationSubscription>> DeliveringAsync(string sourceCollection, CancellationToken cancellationToken);

    /// <summary>Every subscription of <paramref name="consumer"/>.</summary>
    Task<IReadOnlyList<SparkReplicationSubscription>> OfConsumerAsync(string consumer, CancellationToken cancellationToken);

    /// <summary>Every delivering subscription (the reconcile's list).</summary>
    Task<IReadOnlyList<SparkReplicationSubscription>> AllDeliveringAsync(CancellationToken cancellationToken);

    /// <summary>Suspends a subscription whose grant or read right is gone (§4.2.6), and redeploys its consumer.</summary>
    Task SuspendAsync(string consumer, string targetCollection, string reason, CancellationToken cancellationToken);
}

internal sealed partial class ReplicaSubscriptions : IReplicaSubscriptions
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IModuleGrants grants;
    [Inject] private readonly IReplicaSourceTypes sourceTypes;
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaSubscriptions> logger;

    public async Task<ReplicaRegisterOutcome> RegisterAsync(string module, IReadOnlyList<ReplicaRegistrationItem> items, CancellationToken cancellationToken)
    {
        var result = new ReplicaRegistrationResult();
        var parsed = new List<(ReplicaRegistrationItem Item, ReplicaManifest Manifest)>();

        foreach (var item in items)
        {
            if (item is null || !ReplicaScriptRenderer.IsIdentifier(item.TargetCollection) || !ReplicaScriptRenderer.IsIdentifier(item.SourceCollection))
                return Refusal(400, "Invalid registration item");
            if (parsed.Any(p => p.Item.TargetCollection == item.TargetCollection))
                return Refusal(400, $"Target collection '{item.TargetCollection}' is registered twice");

            // F16 closed by construction: the owner never deploys consumer-authored JavaScript. The script is re-rendered
            // from the manifest (whose grammar has member paths only) and must be the posted one, byte for byte.
            if (!ReplicaScriptRenderer.TryParse(item.Manifest, out var manifest, out var error))
                return Refusal(400, $"Invalid manifest for '{item.TargetCollection}': {error}");
            if (manifest!.SourceCollection != item.SourceCollection || manifest.TargetCollection != item.TargetCollection)
                return Refusal(400, $"The manifest of '{item.TargetCollection}' names other collections than the item");
            var script = ReplicaScriptRenderer.RenderScript(manifest);
            if (script != item.Script || ReplicaScriptRenderer.Hash(item.Manifest, script) != item.Hash)
            {
                logger.LogWarning("Registration refused: module '{Module}' posted a script or hash for '{Target}' that its manifest does not render.", module, item.TargetCollection);
                return Refusal(400, $"The script of '{item.TargetCollection}' is not the rendering of its manifest");
            }

            if (sourceTypes.Resolve(item.SourceCollection) is null)
                return Refusal(404, $"Unknown source collection '{item.SourceCollection}'");

            var grant = await grants.CheckAsync(module, item.SourceCollection, manifest, cancellationToken);
            if (!grant.Allowed)
            {
                logger.LogWarning("Registration refused: module '{Module}' reading '{Source}': {Reason}", module, item.SourceCollection, grant.Refusal);
                return Refusal(403, grant.Refusal!);
            }

            parsed.Add((item, manifest));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var autoApprove = hostEnvironment.IsDevelopment();
        var redeploy = false;
        var arrived = new List<SparkReplicationRequest>();

        using (var session = documentStore.OpenAsyncSession())
        {
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var existing = await QueryOfConsumerAsync(session, module, cancellationToken);

            foreach (var (item, manifest) in parsed)
            {
                var id = SparkReplicationSubscription.For(module, item.TargetCollection);
                var subscription = existing.FirstOrDefault(s => s.Id == id);
                if (subscription is null)
                {
                    subscription = new SparkReplicationSubscription
                    {
                        Id = id,
                        ConsumerModule = module,
                        SourceCollection = item.SourceCollection,
                        TargetCollection = item.TargetCollection,
                    };
                    await session.StoreAsync(subscription, id, cancellationToken);
                }

                var answer = await RecordAsync(session, subscription, item, manifest, autoApprove, now, arrived, cancellationToken);
                redeploy |= answer.Status is ReplicaRegistrationStatus.Approved;
                result.Items.Add(answer);
            }

            // F18: what the post no longer lists stops.
            foreach (var gone in existing.Where(s => s.State != ReplicationSubscriptionStates.Withdrawn
                && !parsed.Any(p => p.Item.TargetCollection == s.TargetCollection)))
            {
                gone.State = ReplicationSubscriptionStates.Withdrawn;
                gone.Reason = "No longer registered by the consumer";
                if (gone.PendingRequestId is { } pendingId && await session.LoadAsync<SparkReplicationRequest>(pendingId, cancellationToken) is { } pending)
                    Supersede(pending, "withdrawn by the consumer");
                gone.PendingRequestId = null;
                gone.PendingHash = null;
                result.Withdrawn.Add(gone.TargetCollection);
                redeploy = true;
            }

            if (redeploy)
                await EnqueueDeployAsync(session, module, cancellationToken);

            await session.SaveChangesAsync(cancellationToken);
        }

        services.GetService<ISparkLiveNotifier>()?.CollectionChanged("SparkReplicationRequests");
        await NotifyArrivedAsync(arrived, cancellationToken);
        logger.LogInformation("Module '{Module}' registered {Items}; withdrawn: {Withdrawn}", module,
            string.Join(", ", result.Items.Select(i => $"{i.TargetCollection} {i.Status}")),
            result.Withdrawn.Count == 0 ? "none" : string.Join(", ", result.Withdrawn));
        return new ReplicaRegisterOutcome(200, result);

        ReplicaRegisterOutcome Refusal(int status, string error) => new(status, new ReplicaRegistrationResult { Error = error });
    }

    /// <summary>One item: unchanged, a (re)approval, or a pending request with its diff.</summary>
    private async Task<ReplicaRegistrationItemResult> RecordAsync(
        IAsyncDocumentSession session, SparkReplicationSubscription subscription, ReplicaRegistrationItem item, ReplicaManifest manifest,
        bool autoApprove, DateTime now, List<SparkReplicationRequest> arrived, CancellationToken cancellationToken)
    {
        var reactivated = subscription.State != ReplicationSubscriptionStates.Active;
        subscription.SourceCollection = item.SourceCollection;

        // WD15: the approved shape again — nothing to decide; a pending newer shape the consumer stepped back from is superseded.
        if (subscription.ApprovedHash == item.Hash && subscription.State != ReplicationSubscriptionStates.Suspended)
        {
            subscription.State = ReplicationSubscriptionStates.Active;
            subscription.Reason = null;
            if (subscription.PendingRequestId is { } stepBack && await session.LoadAsync<SparkReplicationRequest>(stepBack, cancellationToken) is { } stepped)
                Supersede(stepped, "the consumer registered the approved shape again");
            subscription.PendingRequestId = null;
            subscription.PendingHash = null;
            return new ReplicaRegistrationItemResult
            {
                TargetCollection = item.TargetCollection,
                Status = reactivated ? ReplicaRegistrationStatus.Approved : ReplicaRegistrationStatus.Unchanged,
                ApprovedHash = subscription.ApprovedHash,
            };
        }

        var requestId = SparkReplicationRequest.For(subscription.ConsumerModule, item.TargetCollection, item.Hash);
        var request = await session.LoadAsync<SparkReplicationRequest>(requestId, cancellationToken);
        if (request?.Status == ReplicationRequestStatuses.Rejected)
        {
            return new ReplicaRegistrationItemResult
            {
                TargetCollection = item.TargetCollection,
                Status = ReplicaRegistrationStatus.Refused,
                ApprovedHash = subscription.IsDelivering ? subscription.ApprovedHash : null,
                Reason = "Rejected: " + (request.Reason ?? "by an administrator"),
            };
        }

        var opened = request is null || request.Status == ReplicationRequestStatuses.Superseded;
        if (opened)
        {
            ReplicaManifest? approved = null;
            if (subscription.ApprovedManifest is { } approvedJson)
                ReplicaScriptRenderer.TryParse(approvedJson, out approved, out _);

            request ??= new SparkReplicationRequest
            {
                Id = requestId,
                ConsumerModule = subscription.ConsumerModule,
                SourceCollection = item.SourceCollection,
                TargetCollection = item.TargetCollection,
                Manifest = item.Manifest,
                Hash = item.Hash,
            };
            request.Status = ReplicationRequestStatuses.Pending;
            request.ApprovedHash = subscription.ApprovedHash;
            request.Diff = ManifestDiff.Describe(approved, manifest);
            request.RequestedAtUtc = now;
            request.Reason = null;
            request.DecidedBy = null;
            request.DecidedAtUtc = null;
            await session.StoreAsync(request, requestId, cancellationToken);
        }

        if (subscription.PendingRequestId is { } previousId && previousId != requestId
            && await session.LoadAsync<SparkReplicationRequest>(previousId, cancellationToken) is { } previous)
            Supersede(previous, "a newer request of the consumer");

        subscription.PendingRequestId = requestId;
        subscription.PendingHash = item.Hash;
        if (subscription.State == ReplicationSubscriptionStates.Withdrawn)
            subscription.State = ReplicationSubscriptionStates.Active;

        // WD16: only the OWNER's host environment approves automatically, never a build configuration or a setting.
        if (request.Status == ReplicationRequestStatuses.Pending && autoApprove)
        {
            await ApproveCoreAsync(session, subscription, request, SparkReplicationRequest.SystemApprover, auto: true, now, cancellationToken);
        }
        else if (request.Status == ReplicationRequestStatuses.Approved)
        {
            // Approved before (a consumer that rolled back and forward again): approved again without asking.
            await ApproveCoreAsync(session, subscription, request, request.DecidedBy, request.AutoApproved, now, cancellationToken);
        }

        // Told once, when the request opens and waits; a re-post of the same pending shape is not news.
        if (opened && request.Status == ReplicationRequestStatuses.Pending)
            arrived.Add(request);

        var approvedNow = subscription.ApprovedHash == item.Hash;
        return new ReplicaRegistrationItemResult
        {
            TargetCollection = item.TargetCollection,
            Status = approvedNow ? ReplicaRegistrationStatus.Approved : ReplicaRegistrationStatus.Pending,
            // Q-W29: while the new shape waits, the consumer keeps receiving the approved one.
            ApprovedHash = subscription.IsDelivering || approvedNow ? subscription.ApprovedHash : null,
        };
    }

    public async Task<ReplicationDecisionOutcome> ApproveAsync(string requestId, string? approver, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var request = await session.LoadAsync<SparkReplicationRequest>(requestId, cancellationToken);
        if (request is null)
            return ReplicationDecisionOutcome.NotFound;
        if (request.Status != ReplicationRequestStatuses.Pending)
            return ReplicationDecisionOutcome.NotPending;

        var subscription = await session.LoadAsync<SparkReplicationSubscription>(
            SparkReplicationSubscription.For(request.ConsumerModule, request.TargetCollection), cancellationToken);
        if (subscription is null || subscription.PendingRequestId != request.Id)
        {
            Supersede(request, "no longer the consumer's request");
            await session.SaveChangesAsync(cancellationToken);
            return ReplicationDecisionOutcome.NotPending;
        }

        // §4.2.6: the grant and every field's read right are checked again at approval.
        if (!ReplicaScriptRenderer.TryParse(request.Manifest, out var manifest, out _)
            || await grants.CheckAsync(request.ConsumerModule, request.SourceCollection, manifest!, cancellationToken) is { Allowed: false })
        {
            await session.SaveChangesAsync(cancellationToken);
            await SuspendAsync(request.ConsumerModule, request.TargetCollection, "No longer granted at approval", cancellationToken);
            return ReplicationDecisionOutcome.NotGranted;
        }

        await ApproveCoreAsync(session, subscription, request, approver, auto: false, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await EnqueueDeployAsync(session, request.ConsumerModule, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        services.GetService<ISparkLiveNotifier>()?.CollectionChanged("SparkReplicationRequests");
        logger.LogInformation("Replication request {RequestId} approved by {Approver}.", requestId, approver);
        return ReplicationDecisionOutcome.Done;
    }

    public async Task<ReplicationDecisionOutcome> RejectAsync(string requestId, string? approver, string? reason, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var request = await session.LoadAsync<SparkReplicationRequest>(requestId, cancellationToken);
        if (request is null)
            return ReplicationDecisionOutcome.NotFound;
        if (request.Status != ReplicationRequestStatuses.Pending)
            return ReplicationDecisionOutcome.NotPending;

        request.Status = ReplicationRequestStatuses.Rejected;
        request.DecidedBy = approver;
        request.DecidedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        request.Reason = reason;

        // PRD §4.2.2: a rejection suspends the subscription and removes its delivery.
        var subscription = await session.LoadAsync<SparkReplicationSubscription>(
            SparkReplicationSubscription.For(request.ConsumerModule, request.TargetCollection), cancellationToken);
        if (subscription is not null && subscription.PendingRequestId == request.Id)
        {
            subscription.PendingRequestId = null;
            subscription.PendingHash = null;
            subscription.State = ReplicationSubscriptionStates.Suspended;
            subscription.Reason = "Rejected: " + (reason ?? "by an administrator");
            await EnqueueDeployAsync(session, request.ConsumerModule, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        services.GetService<ISparkLiveNotifier>()?.CollectionChanged("SparkReplicationRequests");
        logger.LogInformation("Replication request {RequestId} rejected by {Approver}.", requestId, approver);
        return ReplicationDecisionOutcome.Done;
    }

    public async Task SuspendAsync(string consumer, string targetCollection, string reason, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var subscription = await session.LoadAsync<SparkReplicationSubscription>(SparkReplicationSubscription.For(consumer, targetCollection), cancellationToken);
        if (subscription is null || subscription.State == ReplicationSubscriptionStates.Suspended)
            return;
        subscription.State = ReplicationSubscriptionStates.Suspended;
        subscription.Reason = reason;
        await EnqueueDeployAsync(session, consumer, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Suspended the replication of '{Target}' to module '{Consumer}': {Reason}", targetCollection, consumer, reason);
    }

    public async Task<IReadOnlyList<SparkReplicationSubscription>> DeliveringAsync(string sourceCollection, CancellationToken cancellationToken)
        => (await AllDeliveringAsync(cancellationToken)).Where(s => s.SourceCollection == sourceCollection).ToList();

    public async Task<IReadOnlyList<SparkReplicationSubscription>> AllDeliveringAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var all = await session.Advanced.LoadStartingWithAsync<SparkReplicationSubscription>("SparkReplicationSubscriptions/", pageSize: 1024, token: cancellationToken);
        return all.Where(s => s.IsDelivering).ToList();
    }

    public async Task<IReadOnlyList<SparkReplicationSubscription>> OfConsumerAsync(string consumer, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        return await QueryOfConsumerAsync(session, consumer, cancellationToken);
    }

    /// <summary>A prefix load, not a query: the ids are derived, and a point-read never answers from a stale index.</summary>
    private static async Task<List<SparkReplicationSubscription>> QueryOfConsumerAsync(IAsyncDocumentSession session, string consumer, CancellationToken cancellationToken)
        => [.. await session.Advanced.LoadStartingWithAsync<SparkReplicationSubscription>($"SparkReplicationSubscriptions/{consumer}/", pageSize: 1024, token: cancellationToken)];

    private async Task ApproveCoreAsync(IAsyncDocumentSession session, SparkReplicationSubscription subscription, SparkReplicationRequest request,
        string? approver, bool auto, DateTime now, CancellationToken cancellationToken)
    {
        if (subscription.ApprovedRequestId is { } previousId && previousId != request.Id
            && await session.LoadAsync<SparkReplicationRequest>(previousId, cancellationToken) is { Status: ReplicationRequestStatuses.Approved } previous)
            Supersede(previous, "a newer approved request");

        request.Status = ReplicationRequestStatuses.Approved;
        request.DecidedBy = approver;
        request.DecidedAtUtc ??= now;
        request.AutoApproved = auto;

        subscription.ApprovedManifest = request.Manifest;
        subscription.ApprovedHash = request.Hash;
        subscription.ApprovedRequestId = request.Id;
        subscription.PendingRequestId = null;
        subscription.PendingHash = null;
        subscription.State = ReplicationSubscriptionStates.Active;
        subscription.Reason = null;
    }

    private static void Supersede(SparkReplicationRequest request, string why)
    {
        if (request.Status is ReplicationRequestStatuses.Pending or ReplicationRequestStatuses.Approved)
        {
            request.Status = ReplicationRequestStatuses.Superseded;
            request.Reason = "Superseded: " + why;
        }
    }

    /// <summary>The deploy runs from messaging, in this commit: it retries and survives a restart.</summary>
    private Task EnqueueDeployAsync(IAsyncDocumentSession session, string consumer, CancellationToken cancellationToken)
        => services.GetRequiredService<IMessageOutbox>().EnqueueAsync(session, new ReplicaDeployMessage { Consumer = consumer }, cancellationToken: cancellationToken);

    /// <summary>
    /// Tells the <c>replication:approvers</c> slot that requests wait, after the commit (<see cref="ReplicationRequestNotifier"/>:
    /// the members' open pages, a mail, every <see cref="IReplicationRequestObserver"/>).
    /// </summary>
    private Task NotifyArrivedAsync(List<SparkReplicationRequest> arrived, CancellationToken cancellationToken)
        => arrived.Count == 0
            ? Task.CompletedTask
            : services.GetRequiredService<IReplicationRequestNotifier>().NotifyArrivedAsync(arrived, cancellationToken);
}
