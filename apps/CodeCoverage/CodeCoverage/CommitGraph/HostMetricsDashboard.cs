using CodeCoverage.Entities;
using MintPlayer.Spark.Dashboards;
using Raven.Client.Documents;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// The host metrics dashboard (#504, plan M5b, <c>docs/spark_dashboards_PRD.md</c> §9): a live virtual persistent
/// object (<c>App_Data/Model/HostMetrics.json</c>, no documents) that the dashboards package loads, pushes and draws
/// as tiles. Its figures are the package's host metrics and RavenDB statistics plus <see cref="CommitGraphFigures"/>.
/// <c>Read/HostMetrics</c> is granted to <c>dashboards:administrators</c>, which <c>security.json</c> binds to
/// Administrators (<see cref="CoverageAdministrators"/>).
/// </summary>
public sealed class HostMetricsActions(IServiceProvider services) : SparkDashboardActions(services)
{
    public const string TypeName = "HostMetrics";

    /// <summary>The one object id the page answers, as the program unit links it.</summary>
    public const string ObjectId = "main";
}

/// <summary>
/// The commit-graph mirrors on the host metrics dashboard: how many, how big, how many were evicted or refused
/// (app data, exact as of each repository's last fetch, D8) and the mirror volume as the <c>coverage-git</c> sidecar
/// sees it (<c>GET /stats</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The sidecar is optional here.</b> When it is not configured, unreachable or slow, its two values stay empty (the
/// tiles show a dash) and the rest of the page still loads: the package would otherwise blank every value of this
/// provider for one failed call. Nothing of the failure reaches a value; it is logged without the exception's text
/// reaching the page.
/// </para>
/// <para>
/// Not sampled: none of these values keeps a history, and the sidecar's <c>/stats</c> sums every mirror directory.
/// </para>
/// </remarks>
internal sealed class CommitGraphFigures(IDocumentStore store, ICommitGraphSidecar sidecar, ILogger<CommitGraphFigures> logger) : IDashboardProvider
{
    public async Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        if (values.IsSampling)
            return;

        using var session = store.OpenAsyncSession();

        // By id prefix, through the stream: no index, and every state document whatever the collection's size.
        var mirrorCount = 0;
        var mirrorBytes = 0L;
        await using (var stream = await session.Advanced.StreamAsync<RepositoryGraphState>(RepositoryGraphState.Prefix, token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                if (stream.Current.Document is not { HasMirror: true } state) continue;
                mirrorCount++;
                mirrorBytes += state.SizeBytes;
            }
        }

        var metrics = await session.LoadAsync<CommitGraphMetrics>(CommitGraphMetrics.DocumentId, cancellationToken);

        values
            .Set("MirrorCount", mirrorCount)
            .Set("MirrorBytes", mirrorBytes)
            .Set("MirrorEvictions", metrics?.Evictions ?? 0)
            .Set("MirrorRefusals", metrics?.Refusals ?? 0);

        if (await ReadVolumeAsync(cancellationToken) is { } volume)
        {
            values
                .Set("MirrorVolume", DashboardValues.Meter(Math.Max(0, volume.TotalBytes - volume.FreeBytes), volume.TotalBytes))
                .Set("MirrorVolumeFree", volume.FreeBytes);
        }
    }

    private async Task<SidecarStats?> ReadVolumeAsync(CancellationToken cancellationToken)
    {
        if (!sidecar.IsConfigured)
            return null;

        try
        {
            return await sidecar.GetStatsAsync(cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The coverage-git sidecar did not answer /stats ({Error}); the mirror volume stays empty.", ex.GetType().Name);
            return null;
        }
    }
}
