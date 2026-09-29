using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Http;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json;

namespace MintPlayer.Spark.History;

/// <summary>
/// Applies the revisions each model type asks for (T10) to the database at startup, by <b>merging</b>:
/// <c>ConfigureRevisionsOperation</c> replaces the whole configuration — a collection or default it
/// does not mention is gone (measured, spike H4) — so the current configuration is read first, only
/// the configured collections are set, and nothing is sent when they already match (sending an unchanged
/// configuration is still a database-record write, H4).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a type asks for</b> (#460 M16), property by property, first set wins:
/// <c>Spark:History:Types:{type}</c> → the model's <c>revisions</c> block → <c>Spark:History:Revisions</c>
/// (default: 30 days, no count limit, no purge on delete). A type is configured when the model block or
/// <c>Types:{type}:Enabled</c> says so; the model's <c>purgeOnDelete: false</c> cannot be told from
/// "unset", so it defers to the default. A limit of <c>0</c> means "none" (RavenDB's null).
/// </para>
/// <para>
/// Kept as is: the <c>Default</c>, every collection no model type configures, and every setting History
/// does not express (<c>MaximumRevisionsToDeleteUponDocumentUpdate</c>). A type without either source
/// leaves its collection alone — removing the block does not disable revisions (model synchronization
/// never deletes either, #253).
/// </para>
/// <para>
/// ⚠️ RavenDB's licence caps what a collection may ask for — measured on Community (spike H1): at most
/// 2 <c>minimumRevisionsToKeep</c>, at most 45 days <c>minimumRevisionAgeToKeep</c>, and no
/// <b>enabled</b> default configuration. When the server reports a Community licence the limits are
/// checked <b>before</b> anything is sent, and startup refuses naming every type, setting and source;
/// any other refusal (a licence type this check does not know) is RavenDB's own, also naming the escape
/// hatch <c>Spark:History:ConfigureRevisions=false</c>.
/// </para>
/// </remarks>
internal static class RevisionsConfigurator
{
    public static void Apply(IDocumentStore store, IModelLoader modelLoader, SparkHistoryOptions options, ILogger logger)
        => ApplyAsync(store, modelLoader, options, logger).GetAwaiter().GetResult();

    /// <summary>Returns the collections it changed (empty when everything already matched).</summary>
    internal static async Task<IReadOnlyList<string>> ApplyAsync(IDocumentStore store, IModelLoader modelLoader, SparkHistoryOptions options, ILogger logger)
    {
        var types = modelLoader.GetEntityTypes().ToList();
        foreach (var name in options.Types.Keys.Where(k => !types.Any(t => string.Equals(t.Name, k, StringComparison.OrdinalIgnoreCase))))
            logger.LogWarning("Spark:History:Types:{Type} names no model type; ignored.", name);

        var wanted = new List<WantedRevisions>();
        foreach (var definition in types)
        {
            if (Resolve(definition, options) is not { } revisions)
                continue;
            var clrType = HistoryTypes.Resolve(definition.ClrType);
            if (clrType is null)
            {
                logger.LogWarning("Model type {Type} declares revisions but its CLR type {ClrType} was not found; skipped.", definition.Name, definition.ClrType);
                continue;
            }
            wanted.Add(new(store.Conventions.FindCollectionName(clrType), definition.Name, revisions));
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
        var changed = new List<WantedRevisions>();
        foreach (var item in wanted)
        {
            // Collection names compare case-insensitively in RavenDB; reuse the stored key's spelling.
            var key = configuration.Collections.Keys.FirstOrDefault(k => string.Equals(k, item.Collection, StringComparison.OrdinalIgnoreCase)) ?? item.Collection;
            configuration.Collections.TryGetValue(key, out var existing);
            if (existing is not null && Matches(existing, item.Revisions))
                continue;

            existing ??= new RevisionsCollectionConfiguration();
            existing.Disabled = !item.Revisions.Enabled;
            existing.MinimumRevisionsToKeep = item.Revisions.MinimumRevisionsToKeep;
            existing.MinimumRevisionAgeToKeep = item.Revisions.MinimumRevisionAgeToKeep;
            existing.PurgeOnDelete = item.Revisions.PurgeOnDelete;
            configuration.Collections[key] = existing;
            changed.Add(item with { Collection = key });
        }

        if (changed.Count == 0)
        {
            logger.LogDebug("Revisions configuration already matches ({Count} collection(s)).", wanted.Count);
            return [];
        }

        // Checked before sending, so the refusal names every type, setting and source at once instead of
        // RavenDB's first "exceeds the licensed one".
        if (await ReadLicenceLimitsAsync(store, logger) is { } limits
            && LicenceProblems(changed, limits) is { Count: > 0 } problems)
            throw LicenceRefusal(limits, problems);

        try
        {
            await store.Maintenance.SendAsync(new ConfigureRevisionsOperation(configuration));
        }
        catch (Exception ex)
        {
            throw Refused($"configure revisions for {string.Join(", ", changed.Select(c => c.Label))}", ex);
        }

        logger.LogInformation("Revisions configured for {Collections}.", string.Join(", ", changed.Select(c => c.Label)));
        return changed.Select(c => c.Label).ToList();
    }

    /// <summary>One collection's wanted configuration.</summary>
    internal sealed record WantedRevisions(string Collection, string Type, EffectiveRevisions Revisions)
    {
        public string Label => $"{Collection} ({Type})";
    }

    /// <summary>The merged settings for one type, each with where it came from (for the refusal message).</summary>
    internal sealed record EffectiveRevisions(
        bool Enabled,
        long? MinimumRevisionsToKeep, string? MinimumRevisionsToKeepSource,
        TimeSpan? MinimumRevisionAgeToKeep, string? MinimumRevisionAgeToKeepSource,
        bool PurgeOnDelete);

    /// <summary>
    /// What <paramref name="definition"/> asks for: <c>Spark:History:Types:{type}</c> → the model's block →
    /// <c>Spark:History:Revisions</c>; null when neither the block nor <c>Types:{type}:Enabled</c> configures it.
    /// </summary>
    internal static EffectiveRevisions? Resolve(EntityTypeDefinition definition, SparkHistoryOptions options)
    {
        options.Types.TryGetValue(definition.Name, out var type);
        var model = definition.Revisions;
        if (model is null && type?.Enabled is null)
            return null;

        var typeSource = $"Spark:History:Types:{definition.Name}";
        var (count, countSource) = First(
            (type?.MinimumRevisionsToKeep, typeSource),
            (model?.MinimumRevisionsToKeep, "the model's \"revisions\" block"),
            (options.Revisions.MinimumRevisionsToKeep, "Spark:History:Revisions"));
        var (age, ageSource) = First(
            (type?.MinimumRevisionAgeToKeep, typeSource),
            (model?.MinimumRevisionAgeToKeep, "the model's \"revisions\" block"),
            (options.Revisions.MinimumRevisionAgeToKeep, "Spark:History:Revisions"));

        return new EffectiveRevisions(
            Enabled: type?.Enabled ?? model?.Enabled ?? false,
            MinimumRevisionsToKeep: count is > 0 ? count : null, count is > 0 ? countSource : null,
            MinimumRevisionAgeToKeep: age is { } a && a > TimeSpan.Zero ? a : null, age is { } b && b > TimeSpan.Zero ? ageSource : null,
            PurgeOnDelete: type?.PurgeOnDelete ?? (model?.PurgeOnDelete == true ? true : options.Revisions.PurgeOnDelete ?? false));
    }

    private static (T? Value, string? Source) First<T>(params (T? Value, string Source)[] candidates) where T : struct
    {
        foreach (var (value, source) in candidates)
            if (value.HasValue)
                return (value, source);
        return (null, null);
    }

    private static bool Matches(RevisionsCollectionConfiguration existing, EffectiveRevisions revisions)
        => existing.Disabled == !revisions.Enabled
           && existing.MinimumRevisionsToKeep == revisions.MinimumRevisionsToKeep
           && existing.MinimumRevisionAgeToKeep == revisions.MinimumRevisionAgeToKeep
           && existing.PurgeOnDelete == revisions.PurgeOnDelete;

    /// <summary>A licence's caps on a collection's revision limits.</summary>
    internal sealed record LicenceLimits(string LicenceType, long MaxRevisionsToKeep, TimeSpan MaxRevisionAgeToKeep);

    /// <summary>Community's caps, measured in spike H1 ("exceeds the licensed one '2'" / "'45'").</summary>
    internal static readonly LicenceLimits Community = new("Community", 2, TimeSpan.FromDays(45));

    /// <summary>
    /// Every enabled collection whose limits exceed <paramref name="limits"/>, as "Type (Collection):
    /// Setting = value (from source)". A collection with <b>no</b> limit is within them — H1 measured
    /// Community accepting a collection without limits.
    /// </summary>
    internal static IReadOnlyList<string> LicenceProblems(IEnumerable<WantedRevisions> wanted, LicenceLimits limits)
    {
        var problems = new List<string>();
        foreach (var item in wanted.Where(w => w.Revisions.Enabled))
        {
            var r = item.Revisions;
            if (r.MinimumRevisionsToKeep is { } count && count > limits.MaxRevisionsToKeep)
                problems.Add($"{item.Type} ({item.Collection}): MinimumRevisionsToKeep = {count} (from {r.MinimumRevisionsToKeepSource})");
            if (r.MinimumRevisionAgeToKeep is { } age && age > limits.MaxRevisionAgeToKeep)
                problems.Add($"{item.Type} ({item.Collection}): MinimumRevisionAgeToKeep = {age.TotalDays:0.##} days (from {r.MinimumRevisionAgeToKeepSource})");
        }
        return problems;
    }

    /// <summary>The startup refusal for <see cref="LicenceProblems"/>: every problem, and the three ways out.</summary>
    internal static InvalidOperationException LicenceRefusal(LicenceLimits limits, IReadOnlyList<string> problems) => new(
        $"History refuses to start: the RavenDB {limits.LicenceType} licence allows at most {limits.MaxRevisionsToKeep} revisions " +
        $"and {limits.MaxRevisionAgeToKeep.TotalDays:0} days of revision age per collection, and the configured revisions ask for more — " +
        string.Join("; ", problems) + ". Lower them (Spark:History:Types:{type}, the model's \"revisions\" block, or " +
        "Spark:History:Revisions), use a licence that allows them, or set Spark:History:ConfigureRevisions=false and manage " +
        "revisions by hand.");

    /// <summary>
    /// The caps for the server's licence: <see cref="Community"/> when <c>GET /license/status</c> reports
    /// <c>Type: Community</c>, else none (the other tiers measured here — Developer, and AGPL before a licence
    /// is applied — accepted every limit). An unreadable status checks nothing: RavenDB still refuses on send.
    /// </summary>
    private static async Task<LicenceLimits?> ReadLicenceLimitsAsync(IDocumentStore store, ILogger logger)
    {
        try
        {
            var type = await store.Maintenance.Server.SendAsync(new GetLicenceTypeOperation());
            return string.Equals(type, "Community", StringComparison.OrdinalIgnoreCase) ? Community : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The RavenDB licence status could not be read; the revision limits are left to RavenDB to check.");
            return null;
        }
    }

    private static InvalidOperationException Refused(string what, Exception inner) => new(
        $"History could not {what}: {inner.Message.Split('\n')[0].Trim()} " +
        "Configuring revisions is a RavenDB database-admin operation, and the licence caps the limits a " +
        "collection may ask for (Community: 2 revisions, 45 days, no enabled default). Fix the limits " +
        "(Spark:History:Types:{type}, the model's \"revisions\" block, Spark:History:Revisions) or the certificate, " +
        "or set Spark:History:ConfigureRevisions=false and manage revisions by hand.", inner);

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
