using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session;
using Raven.Client.Http;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// RavenDB's <see cref="ISparkRevisions"/> (#536 S8): RavenDB's own revisions. Moved here from History
/// (<c>RevisionsConfigurator</c>, <c>SparkHistory</c>) and SoftDelete (<c>RavenSoftDeleteRevisions</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Configure merges</b>: <c>ConfigureRevisionsOperation</c> replaces the whole configuration (a collection or default it
/// does not mention is gone, #460 spike H4), so the current one is read first, only the named collections are set, and
/// nothing is sent when they already match (an unchanged configuration is still a database-record write, H4). Every
/// setting Spark does not express (<c>MaximumRevisionsToDeleteUponDocumentUpdate</c>, the <c>Default</c>) is kept.
/// </para>
/// <para>
/// <b>Delete</b> is a database-admin operation while deleting the document is not; <see cref="EnsureCanDeleteAsync"/>
/// probes the real operation on an id that names nothing (no effect), once per process.
/// </para>
/// </remarks>
[Register(typeof(ISparkRevisions), ServiceLifetime.Singleton)]
internal sealed partial class RavenSparkRevisions : ISparkRevisions
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<RavenSparkRevisions> logger;

    private volatile bool canDelete;

    /// <summary>Community's caps, measured in #460 spike H1 ("exceeds the licensed one '2'" / "'45'").</summary>
    internal static readonly SparkRevisionLimits Community = new("RavenDB Community licence", 2, TimeSpan.FromDays(45));

    public async Task<IReadOnlyList<string>> ConfigureAsync(IReadOnlyList<SparkRevisionCollection> collections, CancellationToken cancellationToken = default)
    {
        if (collections.Count == 0)
            return [];

        var configuration = await store.Maintenance.SendAsync(new GetRevisionsConfigurationOperation(), cancellationToken) ?? new RevisionsConfiguration();
        configuration.Collections ??= new Dictionary<string, RevisionsCollectionConfiguration>();
        var changed = new List<string>();
        foreach (var item in collections)
        {
            // Collection names compare case-insensitively in RavenDB; reuse the stored key's spelling.
            var key = configuration.Collections.Keys.FirstOrDefault(k => string.Equals(k, item.Collection, StringComparison.OrdinalIgnoreCase)) ?? item.Collection;
            configuration.Collections.TryGetValue(key, out var existing);
            var wanted = item.Settings;
            if (existing is not null
                && existing.Disabled == !wanted.Enabled
                && existing.MinimumRevisionsToKeep == wanted.MinimumRevisionsToKeep
                && existing.MinimumRevisionAgeToKeep == wanted.MinimumRevisionAgeToKeep
                && existing.PurgeOnDelete == wanted.PurgeOnDelete)
                continue;

            existing ??= new RevisionsCollectionConfiguration();
            existing.Disabled = !wanted.Enabled;
            existing.MinimumRevisionsToKeep = wanted.MinimumRevisionsToKeep;
            existing.MinimumRevisionAgeToKeep = wanted.MinimumRevisionAgeToKeep;
            existing.PurgeOnDelete = wanted.PurgeOnDelete;
            configuration.Collections[key] = existing;
            changed.Add(item.Label);
        }

        if (changed.Count > 0)
            await store.Maintenance.SendAsync(new ConfigureRevisionsOperation(configuration), cancellationToken);
        return changed;
    }

    /// <summary>
    /// <see cref="Community"/> when <c>GET /license/status</c> reports <c>Type: Community</c>, else none (Developer, and
    /// AGPL before a licence is applied, accepted every limit). An unreadable status caps nothing: RavenDB still refuses.
    /// </summary>
    public async Task<SparkRevisionLimits?> GetLimitsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var type = await store.Maintenance.Server.SendAsync(new GetLicenceTypeOperation(), cancellationToken);
            return string.Equals(type, "Community", StringComparison.OrdinalIgnoreCase) ? Community : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The RavenDB licence status could not be read; the revision limits are left to RavenDB to check.");
            return null;
        }
    }

    public async Task<IReadOnlyList<SparkRevisionInfo>> ListAsync(Type entityType, string id, int skip, int take, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var metadata = await session.Advanced.Revisions.GetMetadataForAsync(id, Math.Max(0, skip), Math.Max(0, take), cancellationToken);
        var result = new List<SparkRevisionInfo>(metadata.Count);
        foreach (var item in metadata)
        {
            if (!item.TryGetValue("@change-vector", out var cv) || cv?.ToString() is not { Length: > 0 } changeVector)
                continue;
            var flags = item.TryGetValue("@flags", out var f) ? f?.ToString() ?? string.Empty : string.Empty;
            DateTimeOffset? at = item.TryGetValue("@last-modified", out var lm)
                && DateTimeOffset.TryParse(lm?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            result.Add(new(changeVector, at, flags.Contains("DeleteRevision", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    /// <remarks>A change vector is not scoped to a document: RavenDB returns another document's revision for it (spike
    /// H1), so each one's <c>@id</c> and <c>@collection</c> are checked.</remarks>
    public async Task<IReadOnlyDictionary<string, object>> GetAsync(Type entityType, string id, IReadOnlyCollection<string> versions, CancellationToken cancellationToken = default)
    {
        var wanted = versions.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return result;

        using var session = store.OpenAsyncSession();
        // Typed, never <object>: an untyped load of a document without @Raven-Clr-Type is a JObject.
        var loaded = await (Task<Dictionary<string, object?>>)GetTypedMethod.MakeGenericMethod(entityType).Invoke(null, [session, wanted])!;
        var collection = store.Conventions.FindCollectionName(entityType);
        foreach (var (version, revision) in loaded)
        {
            if (revision is null)
                continue;
            var metadata = session.Advanced.GetMetadataFor(revision);
            var revisionId = metadata.TryGetValue("@id", out var rid) ? rid?.ToString() : null;
            var revisionCollection = metadata.TryGetValue("@collection", out var rc) ? rc?.ToString() : null;
            // A delete revision loads as an empty document: it has no content (the interface's contract).
            var flags = metadata.TryGetValue("@flags", out var f) ? f?.ToString() ?? string.Empty : string.Empty;
            if (string.Equals(revisionId, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(revisionCollection, collection, StringComparison.OrdinalIgnoreCase)
                && !flags.Contains("DeleteRevision", StringComparison.OrdinalIgnoreCase))
                result[version] = revision;
        }
        return result;
    }

    private static readonly MethodInfo GetTypedMethod = typeof(RavenSparkRevisions).GetMethod(nameof(GetTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <remarks>
    /// A change vector that names no revision makes the server answer 404, and RavenDB 7.2's client then throws a
    /// <see cref="NullReferenceException"/> from <c>GetRevisions&lt;T&gt;</c> (measured, #536 S8). A version is caller
    /// input, so that reads as "absent": the batch is retried one change vector at a time, each unknown one skipped.
    /// </remarks>
    private static async Task<Dictionary<string, object?>> GetTypedAsync<T>(IAsyncDocumentSession session, List<string> changeVectors) where T : class
    {
        try
        {
            var loaded = await session.Advanced.Revisions.GetAsync<T>(changeVectors);
            return loaded.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
        }
        catch (NullReferenceException)
        {
            var found = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var changeVector in changeVectors)
            {
                try
                {
                    if (await session.Advanced.Revisions.GetAsync<T>(changeVector) is { } revision)
                        found[changeVector] = revision;
                }
                catch (NullReferenceException)
                {
                    // names no revision
                }
            }
            return found;
        }
    }

    public async Task EnsureCanDeleteAsync(CancellationToken cancellationToken = default)
    {
        if (canDelete)
            return;
        try
        {
            await store.Maintenance.SendAsync(new DeleteRevisionsOperation($"spark-purge-probe/{Guid.NewGuid():N}", removeForceCreatedRevisions: true), cancellationToken);
            canDelete = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Deleting revisions failed on a probe. DeleteRevisionsOperation is a RavenDB database-admin operation: give the " +
                "client certificate database-admin access on '{Database}'.", store.Database);
            throw new InvalidOperationException(
                "This RavenDB connection may not delete revisions (DeleteRevisionsOperation needs database-admin access).", ex);
        }
    }

    /// <remarks>After the document, never before: deleting revisions first and the document second writes a fresh delete
    /// revision (#460 spike H1). Force-created revisions only go with the flag.</remarks>
    public async Task<long> DeleteAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => (await store.Maintenance.SendAsync(new DeleteRevisionsOperation(id, removeForceCreatedRevisions: true), cancellationToken)).TotalDeletes;

    /// <summary><c>GET /license/status</c> → its <c>Type</c> (<c>Community</c>, <c>Developer</c>, …).</summary>
    internal sealed class GetLicenceTypeOperation : IServerOperation<string?>
    {
        public RavenCommand<string?> GetCommand(DocumentConventions conventions, JsonOperationContext context) => new Command();

        private sealed class Command : RavenCommand<string?>
        {
            public override bool IsReadRequest => true;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/license/status";
                return new HttpRequestMessage { Method = HttpMethod.Get };
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject? response, bool fromCache)
            {
                if (response is null) return;
                using var json = JsonDocument.Parse(response.ToString());
                Result = json.RootElement.TryGetProperty("Type", out var type) ? type.ToString() : null;
            }
        }
    }

    /// <summary>
    /// <c>GET /databases/{db}/revisions/config</c>, the database-level read of the revisions configuration (RavenDB 7.2.6's
    /// client has no operation for it). 404 (no configuration yet) reads as <see langword="null"/>. Measured in H4.
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
