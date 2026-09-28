using System.Net.Http;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Http;
using Sparrow.Json;

namespace MintPlayer.Spark.History;

/// <summary>
/// Applies the model's <c>revisions</c> blocks (T10) to the database at startup, by <b>merging</b>:
/// <c>ConfigureRevisionsOperation</c> replaces the whole configuration — a collection or default it
/// does not mention is gone (measured, spike H4) — so the current configuration is read first, only
/// the model's collections are set, and nothing is sent when they already match (sending an unchanged
/// configuration is still a database-record write, H4).
/// </summary>
/// <remarks>
/// <para>
/// Kept as is: the <c>Default</c>, every collection no model type configures, and every setting the
/// model does not express (<c>MaximumRevisionsToDeleteUponDocumentUpdate</c>). A type without a
/// <c>revisions</c> block leaves its collection alone — removing the block does not disable
/// revisions (model synchronization never deletes either, #253).
/// </para>
/// <para>
/// ⚠️ RavenDB's licence caps what a collection may ask for — measured on Community (spike H1): at most
/// 2 <c>minimumRevisionsToKeep</c>, at most 45 days <c>minimumRevisionAgeToKeep</c>, and no
/// <b>enabled</b> default configuration. A model asking for more refuses startup, naming the type.
/// </para>
/// </remarks>
internal static class RevisionsConfigurator
{
    public static void Apply(IDocumentStore store, IModelLoader modelLoader, ILogger logger)
        => ApplyAsync(store, modelLoader, logger).GetAwaiter().GetResult();

    /// <summary>Returns the collections it changed (empty when everything already matched).</summary>
    internal static async Task<IReadOnlyList<string>> ApplyAsync(IDocumentStore store, IModelLoader modelLoader, ILogger logger)
    {
        var wanted = new List<(string Collection, string Type, EntityRevisionsDefinition Revisions)>();
        foreach (var definition in modelLoader.GetEntityTypes())
        {
            if (definition.Revisions is not { } revisions)
                continue;
            var clrType = HistoryTypes.Resolve(definition.ClrType);
            if (clrType is null)
            {
                logger.LogWarning("Model type {Type} declares revisions but its CLR type {ClrType} was not found; skipped.", definition.Name, definition.ClrType);
                continue;
            }
            wanted.Add((store.Conventions.FindCollectionName(clrType), definition.Name, revisions));
        }

        if (wanted.Count == 0)
            return [];

        RevisionsConfiguration configuration;
        try
        {
            configuration = await store.Maintenance.SendAsync(new GetRevisionsConfigurationOperation()) ?? new RevisionsConfiguration();
        }
        catch (Exception ex)
        {
            throw Refused("read the database's revisions configuration", ex);
        }

        configuration.Collections ??= new Dictionary<string, RevisionsCollectionConfiguration>();
        var changed = new List<string>();
        foreach (var (collection, type, revisions) in wanted)
        {
            // Collection names compare case-insensitively in RavenDB; reuse the stored key's spelling.
            var key = configuration.Collections.Keys.FirstOrDefault(k => string.Equals(k, collection, StringComparison.OrdinalIgnoreCase)) ?? collection;
            configuration.Collections.TryGetValue(key, out var existing);
            if (existing is not null && Matches(existing, revisions))
                continue;

            existing ??= new RevisionsCollectionConfiguration();
            existing.Disabled = !revisions.Enabled;
            existing.MinimumRevisionsToKeep = revisions.MinimumRevisionsToKeep;
            existing.MinimumRevisionAgeToKeep = revisions.MinimumRevisionAgeToKeep;
            existing.PurgeOnDelete = revisions.PurgeOnDelete;
            configuration.Collections[key] = existing;
            changed.Add($"{key} ({type})");
        }

        if (changed.Count == 0)
        {
            logger.LogDebug("Revisions configuration already matches the model ({Count} collection(s)).", wanted.Count);
            return [];
        }

        try
        {
            await store.Maintenance.SendAsync(new ConfigureRevisionsOperation(configuration));
        }
        catch (Exception ex)
        {
            throw Refused($"configure revisions for {string.Join(", ", changed)}", ex);
        }

        logger.LogInformation("Revisions configured from the model for {Collections}.", string.Join(", ", changed));
        return changed;
    }

    private static bool Matches(RevisionsCollectionConfiguration existing, EntityRevisionsDefinition revisions)
        => existing.Disabled == !revisions.Enabled
           && existing.MinimumRevisionsToKeep == revisions.MinimumRevisionsToKeep
           && existing.MinimumRevisionAgeToKeep == revisions.MinimumRevisionAgeToKeep
           && existing.PurgeOnDelete == revisions.PurgeOnDelete;

    private static InvalidOperationException Refused(string what, Exception inner) => new(
        $"History could not {what}: {inner.Message.Split('\n')[0].Trim()} " +
        "Configuring revisions is a RavenDB database-admin operation, and the licence caps the limits a " +
        "collection may ask for (Community: 2 revisions, 45 days, no enabled default). Fix the model's " +
        "\"revisions\" block or the certificate, or set Spark:History:ConfigureRevisions=false and manage " +
        "revisions by hand.", inner);

    /// <summary>
    /// <c>GET /databases/{db}/revisions/config</c> — the database-level read of the revisions
    /// configuration (RavenDB 7.2.6's client has no operation for it; <c>GetDatabaseRecordOperation</c>
    /// is a server-wide one). 404 (no configuration yet) reads as <see langword="null"/>. Measured in H4.
    /// </summary>
    internal sealed class GetRevisionsConfigurationOperation : IMaintenanceOperation<RevisionsConfiguration?>
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
