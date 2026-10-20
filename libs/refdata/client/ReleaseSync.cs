using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.ReferenceData.Releases;
using Raven.Client.Documents;

namespace MintPlayer.Spark.ReferenceData.Client;

/// <summary>Release files served by an instance (<c>/api/v1/releases/{release}/…</c>), read through the client's HTTP client.</summary>
internal sealed class HttpReleaseFileStore(HttpClient http, Uri baseAddress, string release) : IReleaseFileStore
{
    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(baseAddress, $"api/v1/releases/{Uri.EscapeDataString(release)}/{path}"), cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : null;
    }

    public Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken) => throw new NotSupportedException("An instance's releases are read-only.");
}

/// <summary>
/// The consumer's release sync (PRD §4.7, RD6): the same RD1 mechanism pointed at the instance's manifest.
/// <list type="number">
/// <item>The manifest of <c>/api/v1/releases/latest</c>, its signature verified when a public key is configured (RS17).</item>
/// <item>A new release: the delta from the release this database holds when the manifest carries one, else the full
/// files; a full release that drops an id the database holds is refused. Applied with skip-if-unchanged (a first load
/// through BulkInsert), so re-applying writes nothing.</item>
/// <item>The <b>periodic per-collection checksum</b>, on every run: the digest of what the database holds per release
/// file is compared with the manifest; a mismatch (a delta that did not reproduce the target, a document edited behind
/// the sync's back) re-imports that file in full.</item>
/// <item><c>refdata/state</c> last.</item>
/// </list>
/// </summary>
public sealed class ReleaseSyncService(
    IHttpClientFactory httpClients,
    IDocumentStore store,
    IOptions<SparkReferenceDataClientOptions> options,
    ILogger<ReleaseSyncService> logger)
{
    public async Task<ReleaseApplyResult?> SyncAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.BaseAddress is not { } baseAddress)
            return null;
        var http = httpClients.CreateClient(ReferenceDataClient.HttpClientName);
        var verify = ReleaseSignatures.Verifier(settings.PublicKey is { Length: > 0 } key ? ReleaseSignatures.DecodeKey(key, "release public key") : null);

        using var manifestResponse = await http.GetAsync(new Uri(baseAddress, "api/v1/releases/latest"), cancellationToken);
        if (!manifestResponse.IsSuccessStatusCode)
            return null;
        var latest = (await System.Text.Json.JsonSerializer.DeserializeAsync<ReleaseManifest>(await manifestResponse.Content.ReadAsStreamAsync(cancellationToken), ReleaseFormat.JsonOptions, cancellationToken))?.Release;
        if (latest is null)
            return null;

        var files = new HttpReleaseFileStore(http, baseAddress, latest);
        var countries = settings.Sync.Countries.Select(c => c.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var collections = settings.Sync.Collections.ToHashSet(StringComparer.Ordinal);
        bool Include(ReleaseFile file) => (countries.Count == 0 || countries.Contains(file.Country) || file.Country == "_") && collections.Contains(file.Collection);

        RefDataState? state;
        using (var session = store.OpenAsyncSession())
            state = await session.LoadAsync<RefDataState>(RefDataState.DocumentId, cancellationToken);

        var result = new ReleaseApplyResult(0, 0, 0);
        if (state?.Release != latest)
        {
            var release = await ReleaseReader.ReadAsync(files, cancellationToken, verify, Include, deltaFrom: state?.Release);
            if (release.DeltaFrom is null)
            {
                // Never delete (§4.6): a full release whose id set lacks an id this database holds is refused.
                ReleaseReader.RefuseDroppedIds(await HeldIdsAsync(countries, collections, cancellationToken), release);
            }
            result = await ReleaseApplier.ApplyAsync(store, release, ReleaseOverwrite.Always, cancellationToken);
            logger.LogInformation("Reference data synced to {Release} ({Mode}): {Added} added, {Updated} updated, {Skipped} unchanged.",
                latest, release.DeltaFrom is null ? "full" : "delta from " + release.DeltaFrom, result.Added, result.Updated, result.Skipped);
        }

        // The periodic per-collection checksum (§4.7, RS7): every file's local digest against the manifest's.
        var manifest = await ReleaseReader.ReadManifestAsync(files, cancellationToken, verify);
        var included = manifest.Files.Where(Include).ToList();
        var local = await ReleaseApplier.LocalFileDigestsAsync(store, included, cancellationToken);
        var stale = included.Where(f => !string.Equals(local.GetValueOrDefault(f.Path), f.Digest, StringComparison.Ordinal)).ToList();
        if (stale.Count > 0)
        {
            logger.LogWarning("Reference data: {Files} differ from release {Release}; re-importing them in full.", string.Join(", ", stale.Select(f => f.Path)), latest);
            var documents = await ReleaseReader.ReadFilesAsync(files, stale, null, cancellationToken);
            var repair = await ReleaseApplier.ApplyAsync(store, new ReleaseContent { Manifest = manifest, Documents = documents }, ReleaseOverwrite.Always, cancellationToken);
            result = new ReleaseApplyResult(result.Added + repair.Added, result.Updated + repair.Updated, result.Skipped + repair.Skipped);
        }
        return result;
    }

    private async Task<List<string>> HeldIdsAsync(HashSet<string> countries, HashSet<string> collections, CancellationToken cancellationToken)
    {
        var held = new List<string>();
        using var session = store.OpenAsyncSession();
        foreach (var collection in ReleaseFormat.Collections.Where(c => collections.Contains(c.Name)))
        {
            var ids = await session.Advanced.AsyncRawQuery<IdOnly>($"from {MintPlayer.Spark.Abstractions.RqlIdentifier.Collection(collection.Name)} select id() as Id")
                .Take(int.MaxValue).ToListAsync(cancellationToken);
            held.AddRange(ids.Select(i => i.Id).Where(id => countries.Count == 0 || RefIds.CountryOf(id) is not { } cc || countries.Contains(cc)));
        }
        return held;
    }

    private sealed class IdOnly
    {
        public string Id { get; set; } = string.Empty;
    }
}

/// <summary>The schedule of <see cref="ReleaseSyncService"/> (<c>Spark:ReferenceData:Client:Sync:Schedule</c>).</summary>
public sealed class ReleaseSyncJob(ReleaseSyncService sync) : ISparkCronJob
{
    public static string CronSchedule => "0 15 4 * * *";

    public Task RunAsync(CancellationToken cancellationToken) => sync.SyncAsync(cancellationToken);
}
