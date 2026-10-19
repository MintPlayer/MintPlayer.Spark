using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Http;
using Sparrow.Json;

namespace MintPlayer.Spark.Commerce.Inventory.Storage;

/// <summary>
/// The engine's collections and what S18 asks of them: revisions off (a revision per claim write would multiply the
/// hottest writes in the database), and no persistent-object type over them (a Spark CRUD write, an ordinary session,
/// would break the next cluster-wide transaction's guard).
/// </summary>
internal static class InventoryCollections
{
    public static readonly Type[] Types = [typeof(InventoryQuota), typeof(InventoryQuotaShard), typeof(InventoryItem), typeof(InventoryChain), typeof(InventoryClaim)];

    /// <summary>Refuses startup when a persistent-object type maps an engine document: only the engine writes them (S18).</summary>
    public static void Validate(IModelLoader modelLoader)
    {
        var names = Types.Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal);
        var problems = modelLoader.GetEntityTypes()
            .Where(t => t.ClrType is { } clr && names.Contains(clr.Split(',')[0].Trim()))
            .Select(t => $"  - The persistent-object type '{t.Name}' maps the inventory engine's {t.ClrType}. Engine documents are written only by the engine, in cluster-wide transactions (S18); read them through ISparkInventory.")
            .ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException("Commerce inventory cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}

/// <summary>Turns revisions off on the engine's collections at startup, merging into the database's configuration (as Dashboards does).</summary>
internal sealed class InventoryStoreSetup(IDocumentStore store, ILogger<InventoryStoreSetup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var changed = await DisableRevisionsAsync(store, cancellationToken);
            if (changed.Count > 0)
                logger.LogInformation("Turned revisions off on the inventory collections {Collections}.", string.Join(", ", changed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Correctness does not depend on it; the database only grows faster. Say so loudly.
            logger.LogError(ex, "Could not turn revisions off on the inventory collections; every claim write keeps a revision until this is fixed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Returns the collections it changed. <c>ConfigureRevisionsOperation</c> replaces everything, so this is a read-modify-write.</summary>
    internal static async Task<IReadOnlyList<string>> DisableRevisionsAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        var configuration = await store.Maintenance.SendAsync(new GetRevisionsConfigurationOperation(), cancellationToken) ?? new RevisionsConfiguration();
        configuration.Collections ??= new Dictionary<string, RevisionsCollectionConfiguration>();
        var changed = new List<string>();
        foreach (var collection in InventoryCollections.Types.Select(store.Conventions.FindCollectionName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Collection names compare case-insensitively in RavenDB; reuse the stored key's spelling.
            var key = configuration.Collections.Keys.FirstOrDefault(k => string.Equals(k, collection, StringComparison.OrdinalIgnoreCase)) ?? collection;
            if (configuration.Collections.TryGetValue(key, out var existing) && existing is { Disabled: true })
                continue;
            configuration.Collections[key] = new RevisionsCollectionConfiguration { Disabled = true };
            changed.Add(key);
        }
        if (changed.Count > 0)
            await store.Maintenance.SendAsync(new ConfigureRevisionsOperation(configuration), cancellationToken);
        return changed;
    }

    /// <summary>
    /// <c>GET /databases/{db}/revisions/config</c>: RavenDB 7.2.6's client has no operation for it. 404 reads as null.
    /// The same request as History's <c>RevisionsConfigurator</c> and Dashboards' copy, which this package does not reference.
    /// </summary>
    private sealed class GetRevisionsConfigurationOperation : IMaintenanceOperation<RevisionsConfiguration?>
    {
        public RavenCommand<RevisionsConfiguration?> GetCommand(DocumentConventions conventions, JsonOperationContext context)
            => new Command(conventions);

        private sealed class Command(DocumentConventions conventions) : RavenCommand<RevisionsConfiguration?>
        {
            public override bool IsReadRequest => false;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/revisions/config";
                return new HttpRequestMessage { Method = HttpMethod.Get };
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject? response, bool fromCache)
                => Result = response is null ? null : conventions.Serialization.DefaultConverter.FromBlittable<RevisionsConfiguration>(response);
        }
    }
}
