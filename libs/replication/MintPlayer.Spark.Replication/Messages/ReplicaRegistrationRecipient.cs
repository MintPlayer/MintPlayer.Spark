using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Consumer (warehouse §4.2.2): posts every replica this module reads from one owner to its
/// <c>/spark/replicas/register</c>, at the owner's <c>AppUrl</c> from the registry (resolved on each delivery, so a retry
/// reaches a module that registered or moved meanwhile), and stores the owner's answer. Owner not registered or
/// unreachable, 5xx, 429 → <see cref="RetryAfterException"/> on the ladder; 400/403/404/409 → the owner's settled answer,
/// <see cref="NonRetryableException"/> with the reason.
/// </summary>
internal sealed partial class ReplicaRegistrationRecipient : IRecipient<ReplicaRegistrationMessage>
{
    [Inject] private readonly ReplicaCatalog catalog;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IReplicationHttpClientProvider httpClients;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaRegistrationRecipient> logger;

    public async Task HandleAsync(ReplicaRegistrationMessage message, CancellationToken cancellationToken = default)
    {
        var owner = message.Owner;
        var setHash = catalog.SetHash(owner);

        using var session = documentStore.OpenAsyncSession();
        var stateId = ReplicaRegistrationState.For(owner);
        var state = await session.LoadAsync<ReplicaRegistrationState>(stateId, cancellationToken);
        if (state?.IsSettled(setHash) == true)
            return;

        var registration = await moduleDirectory.FindAsync(owner, cancellationToken);
        if (registration is null || string.IsNullOrEmpty(registration.AppUrl))
            throw Retry(message, $"module '{owner}' is not registered in SparkModules yet");

        var request = new ReplicaRegistrationRequest
        {
            RequestingModule = options.Value.ModuleName,
            Items = [.. catalog.FromOwner(owner).Select(d => new ReplicaRegistrationItem
            {
                TargetCollection = d.TargetCollection,
                SourceCollection = d.SourceCollection,
                Manifest = d.Manifest,
                Script = d.Script,
                Hash = d.Hash,
            })],
        };

        var result = await ModuleCalls.PostAsync<ReplicaRegistrationRequest, ReplicaRegistrationResult>(
            httpClients.GetClient(owner), registration.AppUrl, "/spark/replicas/register", request, cancellationToken);

        if (!result.IsSuccess)
        {
            if (result.IsTransient)
                throw Retry(message, $"module '{owner}' answered {(result.StatusCode is { } code ? ((int)code).ToString() : "nothing")}");
            throw new NonRetryableException($"Replica registration with '{owner}' refused with {(int)result.StatusCode!}: {result.Value?.Error ?? result.Body}");
        }

        state ??= new ReplicaRegistrationState { Id = stateId, Owner = owner };
        state.SetHash = setHash;
        state.AnsweredAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        state.Items.Clear();
        foreach (var item in result.Value?.Items ?? [])
        {
            state.Items[item.TargetCollection] = new ReplicaRegistrationAnswer { Status = item.Status, ApprovedHash = item.ApprovedHash, Reason = item.Reason };
            if (item.Status is ReplicaRegistrationStatus.Refused)
                logger.LogWarning("Module '{Owner}' refused replica collection '{Target}': {Reason}", owner, item.TargetCollection, item.Reason);
            else if (item.Status is ReplicaRegistrationStatus.Pending)
                logger.LogInformation("Replica collection '{Target}' waits for approval at '{Owner}'{Delivering}.", item.TargetCollection, owner,
                    item.ApprovedHash is null ? "" : "; the last approved shape keeps flowing");
        }
        await session.StoreAsync(state, stateId, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private RetryAfterException Retry(ReplicaRegistrationMessage message, string why)
        => new(RetryLadder.Next(options.Value.Push.ResolvedRetryLadder, timeProvider.GetUtcNow().UtcDateTime - message.QueuedAtUtc),
            $"Replica registration with '{message.Owner}' deferred: {why}");
}
