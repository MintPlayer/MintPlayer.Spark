using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>The owner's answer to a subscription, with the HTTP status it maps to.</summary>
internal sealed record ReplicaSubscribeOutcome(int StatusCode, ReplicaSubscriptionResult Result);

/// <summary>
/// The owner's book of consumers (warehouse WX2, F18): who reads which contract, at which version, by which transport.
/// The authenticated module is the only one it ever changes, and every destination it deploys to is that module's
/// registry entry (F19).
/// </summary>
internal interface IReplicaSubscriptions
{
    /// <summary>
    /// Replaces <paramref name="module"/>'s subscriptions with <paramref name="items"/>, after checking each contract is
    /// published and granted (<c>Replicate/{contract}</c>), and brings its ETL task in line. Asked inside the module's
    /// request: its principal is the caller.
    /// </summary>
    Task<ReplicaSubscribeOutcome> SubscribeAsync(string module, IReadOnlyList<ReplicaSubscriptionItem> items, Func<string, Task<bool>> isGranted, CancellationToken cancellationToken);

    /// <summary>The consumers of <paramref name="contract"/> (all versions).</summary>
    Task<IReadOnlyList<ReplicaConsumer>> ConsumersAsync(string contract, CancellationToken cancellationToken);

    /// <summary>Withdraws <paramref name="module"/> from <paramref name="contract"/> (a revoked grant), and its ETL transform.</summary>
    Task WithdrawAsync(string module, string contract, CancellationToken cancellationToken);

    /// <summary>Every (module, contract) subscription, for the grant sweep.</summary>
    Task<IReadOnlyList<(string Module, string Contract)>> AllAsync(CancellationToken cancellationToken);
}

internal sealed partial class ReplicaSubscriptions : IReplicaSubscriptions
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ReplicationContractCatalog catalog;
    [Inject] private readonly IReplicationTransportResolver transportResolver;
    [Inject] private readonly IEtlTaskManager etlTaskManager;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaSubscriptions> logger;

    private IEnumerable<string> PublishedNames => catalog.All.Select(c => c.Name).Distinct(StringComparer.Ordinal);

    public async Task<ReplicaSubscribeOutcome> SubscribeAsync(
        string module, IReadOnlyList<ReplicaSubscriptionItem> items, Func<string, Task<bool>> isGranted, CancellationToken cancellationToken)
    {
        var transport = await transportResolver.ResolveAsync(cancellationToken);
        var result = new ReplicaSubscriptionResult { Transport = transport };
        var wanted = new Dictionary<string, (PublishedContract Contract, string TargetCollection)>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            // The target collection becomes code in an ETL script (loadTo{Collection}): a plain identifier or nothing.
            if (item is null || !EtlTransformGenerator.IsIdentifier(item.TargetCollection) || item.Versions is not { Length: > 0 })
                return Refusal(400, "Invalid subscription item");

            var contract = catalog.Highest(item.Contract, item.Versions);
            if (contract is null)
            {
                logger.LogWarning("Subscription refused: module '{Module}' asked for contract '{Contract}' v{Versions}, which this module does not publish.",
                    module, item.Contract, string.Join(",", item.Versions));
                return Refusal(404, $"Unknown contract '{item.Contract}' v{string.Join(",", item.Versions)}");
            }

            // The certificate answers "who are you"; the grant, on the contract itself, answers "what may you read".
            if (!await isGranted(contract.Name))
            {
                logger.LogWarning("Subscription refused: module '{Module}' holds no Replicate/{Contract}.", module, contract.Name);
                return Refusal(403, "Forbidden");
            }

            wanted[contract.Name] = (contract, item.TargetCollection);
        }

        // F19: an ETL destination is the authenticated module's registry entry, never the request.
        if (transport == ReplicationTransport.Etl && wanted.Count > 0)
        {
            var registration = await moduleDirectory.FindAsync(module, cancellationToken);
            if (registration is null || string.IsNullOrEmpty(registration.DatabaseName) || registration.DatabaseUrls.Length == 0)
                return Refusal(409, "The requesting module has no database registered");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        using (var session = documentStore.OpenAsyncSession())
        {
            session.Advanced.UseOptimisticConcurrency = true;
            foreach (var name in PublishedNames)
            {
                var set = await LoadSetAsync(session, name, cancellationToken);
                var had = set.Consumers.RemoveAll(c => string.Equals(c.Module, module, StringComparison.OrdinalIgnoreCase)) > 0;

                if (wanted.TryGetValue(name, out var entry))
                {
                    set.Consumers.Add(new ReplicaConsumer
                    {
                        Module = module,
                        Version = entry.Contract.Version,
                        TargetCollection = entry.TargetCollection,
                        Transport = transport,
                        SubscribedAtUtc = now,
                    });
                    result.Contracts.Add(new ReplicaSubscriptionGranted { Contract = name, Version = entry.Contract.Version });
                }
                else if (had)
                {
                    result.Withdrawn.Add(name);
                }
            }
            await session.SaveChangesAsync(cancellationToken);
        }

        var etl = await SyncEtlTaskAsync(module, transport, cancellationToken);
        if (!etl.Success)
        {
            result.Error = etl.Error;
            return new ReplicaSubscribeOutcome(etl.Error == EtlTaskResult.NotLicensedError ? 409 : 500, result);
        }

        logger.LogInformation("Module '{Module}' subscribed to {Contracts} over {Transport}; withdrawn: {Withdrawn}",
            module, string.Join(", ", result.Contracts.Select(c => $"{c.Contract} v{c.Version}")), transport,
            result.Withdrawn.Count == 0 ? "none" : string.Join(", ", result.Withdrawn));
        return new ReplicaSubscribeOutcome(200, result);

        ReplicaSubscribeOutcome Refusal(int status, string error)
            => new(status, new ReplicaSubscriptionResult { Transport = transport, Error = error });
    }

    public async Task<IReadOnlyList<ReplicaConsumer>> ConsumersAsync(string contract, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var set = await session.LoadAsync<ReplicaSubscriptionSet>(ReplicaSubscriptionSet.For(contract), cancellationToken);
        return set?.Consumers ?? [];
    }

    public async Task WithdrawAsync(string module, string contract, CancellationToken cancellationToken)
    {
        using (var session = documentStore.OpenAsyncSession())
        {
            session.Advanced.UseOptimisticConcurrency = true;
            var set = await session.LoadAsync<ReplicaSubscriptionSet>(ReplicaSubscriptionSet.For(contract), cancellationToken);
            if (set is null || set.Consumers.RemoveAll(c => string.Equals(c.Module, module, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
            await session.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning("Withdrew module '{Module}' from contract '{Contract}': it no longer holds Replicate/{Contract}.", module, contract, contract);
        await SyncEtlTaskAsync(module, await transportResolver.ResolveAsync(cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<(string Module, string Contract)>> AllAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var sets = await session.LoadAsync<ReplicaSubscriptionSet>(PublishedNames.Select(ReplicaSubscriptionSet.For), cancellationToken);
        return sets.Values.Where(s => s is not null)
            .SelectMany(s => s!.Consumers.Select(c => (c.Module, s.Contract)))
            .ToList();
    }

    /// <summary>
    /// Brings the module's ETL task in line with its subscriptions: one task with every ETL subscription's transform,
    /// or no task at all (F18). On the HTTP transport any leftover task is removed.
    /// </summary>
    private async Task<EtlTaskResult> SyncEtlTaskAsync(string module, ReplicationTransport transport, CancellationToken cancellationToken)
    {
        var transforms = new List<EtlTransformScript>();
        if (transport == ReplicationTransport.Etl)
        {
            using var session = documentStore.OpenAsyncSession();
            var sets = await session.LoadAsync<ReplicaSubscriptionSet>(PublishedNames.Select(ReplicaSubscriptionSet.For), cancellationToken);
            foreach (var set in sets.Values.Where(s => s is not null))
            {
                foreach (var consumer in set!.Consumers.Where(c => string.Equals(c.Module, module, StringComparison.OrdinalIgnoreCase)))
                {
                    if (catalog.Find(set.Contract, consumer.Version) is not { EtlExpressible: true } contract)
                        continue;
                    transforms.Add(new EtlTransformScript(
                        $"{contract.Name}-v{contract.Version}-{consumer.TargetCollection}",
                        contract.SourceCollection,
                        contract.ScriptFor(consumer.TargetCollection)));
                }
            }
        }

        if (transforms.Count == 0)
            return await etlTaskManager.RemoveAsync(module, cancellationToken);

        var registration = await moduleDirectory.FindAsync(module, cancellationToken);
        if (registration is null || string.IsNullOrEmpty(registration.DatabaseName) || registration.DatabaseUrls.Length == 0)
            return new EtlTaskResult(false, "The requesting module has no database registered");

        return await etlTaskManager.DeployAsync(new EtlDeployment(module, registration.DatabaseName, registration.DatabaseUrls, transforms), cancellationToken);
    }

    private static async Task<ReplicaSubscriptionSet> LoadSetAsync(IAsyncDocumentSession session, string contract, CancellationToken cancellationToken)
    {
        var id = ReplicaSubscriptionSet.For(contract);
        var set = await session.LoadAsync<ReplicaSubscriptionSet>(id, cancellationToken);
        if (set is null)
        {
            set = new ReplicaSubscriptionSet { Id = id, Contract = contract };
            await session.StoreAsync(set, id, cancellationToken);
        }
        return set;
    }
}
