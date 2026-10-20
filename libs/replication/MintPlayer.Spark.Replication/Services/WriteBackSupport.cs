using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>Sends one write-back to its owner (§4.2.5): the save's synchronous attempt and the queued retries share it.</summary>
internal interface ISyncActionSender
{
    /// <param name="timeout">For the synchronous attempt: past it the edit is queued instead.</param>
    Task<ModuleCallResult<SyncApplyResult>> SendAsync(string owner, SyncApplyRequest request, TimeSpan? timeout, CancellationToken cancellationToken);
}

internal sealed partial class SyncActionSender : ISyncActionSender
{
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IReplicationHttpClientProvider httpClients;

    public async Task<ModuleCallResult<SyncApplyResult>> SendAsync(string owner, SyncApplyRequest request, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
            deadline.CancelAfter(limit);
        try
        {
            var registration = await moduleDirectory.FindAsync(owner, deadline.Token);
            if (registration is null || string.IsNullOrEmpty(registration.AppUrl))
                return new ModuleCallResult<SyncApplyResult>(null, null, $"module '{owner}' is not registered in SparkModules");
            return await ModuleCalls.PostAsync<SyncApplyRequest, SyncApplyResult>(httpClients.GetClient(owner), registration.AppUrl, "/spark/sync/apply", request, deadline.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The owner did not answer in time: no answer, so the edit is queued (and the op id makes a late apply harmless).
            return new ModuleCallResult<SyncApplyResult>(null, null, $"module '{owner}' did not answer in time", ex);
        }
    }
}

/// <summary>Reads the pending rule of a replica document (Q-W28) and its badge facts.</summary>
internal static class SyncPending
{
    /// <summary>
    /// Whether <paramref name="marker"/> still counts: its queued SyncAction runs, or the owner applied the edit and the
    /// replica (whose change vector is <paramref name="replicaChangeVector"/>) has not been rewritten by the delivery yet.
    /// </summary>
    public static bool IsPending(SyncPendingMarker? marker, string? replicaChangeVector, DateTime nowUtc)
    {
        if (marker is null)
            return false;
        if (marker.SyncActionId is not null)
            return true;
        return nowUtc - marker.CreatedAtUtc < SyncPendingMarker.AppliedLifetime
            && replicaChangeVector is not null && replicaChangeVector == marker.ReplicaChangeVector;
    }
}

/// <summary>
/// Tells the initiator how a queued write-back ended without being applied (§4.2.5, owner decision 2026-10-20):
/// the <see cref="SparkSyncAction"/> itself is the durable per-user record ("My pending changes", shown at the next
/// sign-in); its state is pushed live to the initiator's open pages (<c>replication:outcomes</c>); a give-up is also
/// mailed when MailManager is registered; and every <see cref="ISyncActionOutcomeObserver"/> hears it.
/// </summary>
internal interface ISyncOutcomeNotifier
{
    Task NotifyAsync(SparkSyncAction action, CancellationToken cancellationToken);
}

internal sealed partial class SyncOutcomeNotifier : ISyncOutcomeNotifier
{
    /// <summary>The private live topic a terminal outcome is pushed on, per initiator.</summary>
    public const string OutcomesTopic = "replication:outcomes";

    /// <summary>The give-up mail's template (embedded, <c>Mail/SparkReplication/WriteBackGaveUp.mjml</c>).</summary>
    public const string GaveUpTemplate = "SparkReplication/WriteBackGaveUp";

    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly ILogger<SyncOutcomeNotifier> logger;

    public async Task NotifyAsync(SparkSyncAction action, CancellationToken cancellationToken)
    {
        services.GetService<ISparkLiveNotifier>()?.CollectionChanged("SparkSyncActions");
        if (action.InitiatorId is { Length: > 0 } initiator && services.GetService<ISparkLiveTopics>() is { } topics)
        {
            topics.PublishPrivate("user:" + initiator, OutcomesTopic, new SyncOutcomeFrame(
                action.Id!, action.OwnerModule, action.TargetCollection, action.DocumentId, action.Status, action.Reason, action.Attributes));
        }

        var ended = new SyncActionEnded(action.Id!, action.OwnerModule, action.TargetCollection, action.DocumentId,
            action.InitiatorId, action.Status, action.Reason, action.Attributes);
        foreach (var observer in services.GetServices<ISyncActionOutcomeObserver>())
        {
            try { await observer.OnSyncActionEndedAsync(ended, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Write-back outcome observer {Observer} failed for {SyncActionId}.", observer.GetType().Name, action.Id);
            }
        }

        if (action.Status == SyncActionStatuses.GaveUp)
            await MailGaveUpAsync(action, cancellationToken);
    }

    /// <summary>
    /// The give-up mail, when MailManager is registered and the initiator has a confirmed address. The user document
    /// is read as JSON (<c>Email</c>, <c>EmailConfirmed</c>, <c>UserName</c>, <c>PreferredCulture</c>), so replication
    /// does not depend on the Authorization package.
    /// </summary>
    private async Task MailGaveUpAsync(SparkSyncAction action, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer || action.InitiatorId is not { Length: > 0 } initiator)
            return;

        try
        {
            using var session = documentStore.OpenAsyncSession();
            var user = await session.LoadAsync<object>(initiator, cancellationToken);
            if (user is null)
                return;
            var json = ReplicaJson.Serialize(documentStore, user);
            if (!json.TryGetProperty("Email", out var email) || email.ValueKind != JsonValueKind.String
                || !json.TryGetProperty("EmailConfirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True
                || !MailAddress.TryCreate(email.GetString(), out _))
                return;

            await mailer.SendAsync(new SparkMailRequest
            {
                Template = GaveUpTemplate,
                To = email.GetString()!,
                ToName = json.TryGetProperty("UserName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                Culture = json.TryGetProperty("PreferredCulture", out var culture) && culture.ValueKind == JsonValueKind.String ? culture.GetString() : null,
                Data = new
                {
                    owner = action.OwnerModule,
                    collection = action.TargetCollection,
                    document = action.DocumentId,
                    changes = action.Summary,
                    reason = action.Reason,
                    module = options.Value.ModuleName,
                },
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The give-up mail of write-back {SyncActionId} was not sent.", action.Id);
        }
    }
}

/// <summary>What <c>replication:outcomes</c> carries: a small DTO, never a persistent object (live payloads skip row security).</summary>
public sealed record SyncOutcomeFrame(
    string SyncActionId,
    string OwnerModule,
    string TargetCollection,
    string DocumentId,
    string Status,
    string? Reason,
    IReadOnlyList<string> Attributes);

/// <summary>
/// <c>replication:outcomes</c> and <c>replication:requests</c> are private to each signed-in user: their own write-backs'
/// outcomes, and (for a member of <c>replication:approvers</c>) the requests that wait for them; frames are only ever
/// published to the user they are for.
/// </summary>
internal sealed class ReplicationLiveTopicPolicy : ISparkLiveTopicPolicy
{
    public string Prefix => "replication:";

    public bool CanSubscribe(string topic, SparkLiveViewer viewer) => false;

    public bool CanSubscribePrivate(string topic, SparkLiveViewer viewer)
        => topic is SyncOutcomeNotifier.OutcomesTopic or ReplicationRequestNotifier.RequestsTopic && viewer.UserId is { Length: > 0 };
}

/// <summary>Builds the stored and the wire form of write-back changes.</summary>
internal static class SyncChanges
{
    public static SparkSyncActionChange ToStored(SyncPropertyChange change)
        => new() { Property = change.Property, BaseJson = change.Base.GetRawText(), ValueJson = change.Value.GetRawText() };

    public static SyncPropertyChange ToWire(SparkSyncActionChange change)
        => new() { Property = change.Property, Base = Parse(change.BaseJson), Value = Parse(change.ValueJson) };

    public static JsonElement Parse(string? json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json);
        return document.RootElement.Clone();
    }

    /// <summary>A JSON null element.</summary>
    public static JsonElement Null => Parse("null");

    public static async Task<SparkSyncAction?> LoadAsync(IAsyncDocumentSession session, string id, CancellationToken cancellationToken)
        => await session.LoadAsync<SparkSyncAction>(id, cancellationToken);
}
