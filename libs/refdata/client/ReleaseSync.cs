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
/// The consumer's release sync (PRD §4.7, RD6): the same RD1 mechanism pointed at the instance's manifest. A conditional
/// check on <c>/api/v1/releases/latest</c>; an unchanged release ends the run. Otherwise the release is read and
/// verified (file hashes, digests, every document's own hash), refused when it drops an id the database holds, and
/// applied with skip-if-unchanged, so re-applying a release writes nothing. <c>refdata/state</c> is written last.
/// </summary>
/// <remarks>Not built: deltas (full files are always read), the periodic per-collection checksum, and signature verification (RS17).</remarks>
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

        using var manifestResponse = await http.GetAsync(new Uri(baseAddress, "api/v1/releases/latest"), cancellationToken);
        if (!manifestResponse.IsSuccessStatusCode)
            return null;
        var latest = (await System.Text.Json.JsonSerializer.DeserializeAsync<ReleaseManifest>(await manifestResponse.Content.ReadAsStreamAsync(cancellationToken), ReleaseFormat.JsonOptions, cancellationToken))?.Release;
        if (latest is null)
            return null;

        RefDataState? state;
        using (var session = store.OpenAsyncSession())
            state = await session.LoadAsync<RefDataState>(RefDataState.DocumentId, cancellationToken);
        if (state?.Release == latest)
            return new ReleaseApplyResult(0, 0, 0);

        var countries = settings.Sync.Countries.Select(c => c.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var collections = settings.Sync.Collections.ToHashSet(StringComparer.Ordinal);
        var release = await ReleaseReader.ReadAsync(new HttpReleaseFileStore(http, baseAddress, latest), cancellationToken,
            include: file => (countries.Count == 0 || countries.Contains(file.Country) || file.Country == "_") && collections.Contains(file.Collection));

        // Never delete (§4.6): a release whose id set lacks an id this database holds is refused.
        var held = new List<string>();
        using (var session = store.OpenAsyncSession())
            foreach (var collection in ReleaseFormat.Collections.Where(c => collections.Contains(c.Name)))
            {
                var ids = await session.Advanced.AsyncRawQuery<IdOnly>($"from {MintPlayer.Spark.Abstractions.RqlIdentifier.Collection(collection.Name)} select id() as Id")
                    .Take(int.MaxValue).ToListAsync(cancellationToken);
                held.AddRange(ids.Select(i => i.Id).Where(id => countries.Count == 0 || RefIds.CountryOf(id) is not { } cc || countries.Contains(cc)));
            }
        ReleaseReader.RefuseDroppedIds(held, release);

        var result = await ReleaseApplier.ApplyAsync(store, release, ReleaseOverwrite.Always, cancellationToken);
        logger.LogInformation("Reference data synced to {Release}: {Added} added, {Updated} updated, {Skipped} unchanged.", latest, result.Added, result.Updated, result.Skipped);
        return result;
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
