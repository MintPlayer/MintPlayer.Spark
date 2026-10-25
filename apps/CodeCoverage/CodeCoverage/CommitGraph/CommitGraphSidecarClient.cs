using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// The app's side of the <c>coverage-git</c> sidecar's internal API (#504, PRD §4.1, D3/D4/D19).
/// </summary>
/// <remarks>
/// <para>
/// <b>No authentication, by design (D4).</b> The sidecar is reachable only on the internal
/// two-host network <c>coverage-git</c>; the network is the boundary. A credential for the forge
/// travels in the body of a fetch and nowhere else.
/// </para>
/// <para>
/// The path segments are validated against the contract's own grammar before any request is made,
/// so a repository id that does not fit (<c>^[a-z]+$</c> / <c>^[0-9A-Za-z_-]{1,64}$</c>) is never
/// sent rather than refused by the sidecar.
/// </para>
/// </remarks>
public interface ICommitGraphSidecar
{
    /// <summary>False when <c>CommitGraph:SidecarUrl</c> is unset: the feature is off.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Clones the mirror if missing (only with <see cref="SidecarFetchModes.CloneOrFetch"/>, D40), else
    /// fetches it; joins an in-flight fetch (D19). Bounded by
    /// <paramref name="timeout"/> (the quota's plus a margin), and never throws for an outcome the
    /// sidecar reports — only for a transport failure.
    /// </summary>
    Task<SidecarFetchResult> FetchAsync(string repositoryId, SidecarFetchRequest request, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>One page of rows, or why there is none.</summary>
    Task<SidecarGraphResult> GetGraphAsync(string repositoryId, SidecarGraphQuery query, CancellationToken cancellationToken = default);

    /// <summary>Removes the mirror and its snapshots. Idempotent; waits for an in-flight fetch.</summary>
    Task DeleteAsync(string repositoryId, CancellationToken cancellationToken = default);

    /// <summary>The mirror volume's free and total bytes and the mirrors on it (<c>GET /stats</c>, the host metrics dashboard).</summary>
    Task<SidecarStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>A typed <see cref="HttpClient"/> over the sidecar contract.</summary>
public sealed partial class CommitGraphSidecarClient(HttpClient http, IOptions<CommitGraphOptions> options) : ICommitGraphSidecar
{
    /// <summary>A page read is a few hundred milliseconds even on vscode (S3); this only bounds a hung sidecar.</summary>
    private static readonly TimeSpan GraphTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public bool IsConfigured => http.BaseAddress is not null;

    public async Task<SidecarFetchResult> FetchAsync(string repositoryId, SidecarFetchRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = Bounded(timeout, cancellationToken);
        using var response = await http.PostAsJsonAsync($"repos/{Path(repositoryId)}/fetch", request, Json, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SidecarFetchResult>(Json, cts.Token)
            ?? throw new InvalidOperationException("The sidecar answered a fetch with an empty body.");
    }

    public async Task<SidecarGraphResult> GetGraphAsync(string repositoryId, SidecarGraphQuery query, CancellationToken cancellationToken = default)
    {
        using var cts = Bounded(GraphTimeout, cancellationToken);
        using var response = await http.GetAsync($"repos/{Path(repositoryId)}/graph{query.ToQueryString()}", cts.Token);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var page = await response.Content.ReadFromJsonAsync<SidecarGraphPage>(Json, cts.Token)
                    ?? throw new InvalidOperationException("The sidecar answered a graph read with an empty body.");
                return SidecarGraphResult.Ok(page);
            case HttpStatusCode.Gone:
                var stale = await response.Content.ReadFromJsonAsync<SidecarStateBody>(Json, cts.Token);
                return SidecarGraphResult.Stale(stale?.SnapshotId);
            case HttpStatusCode.NotFound:
                var missing = await response.Content.ReadFromJsonAsync<SidecarStateBody>(Json, cts.Token);
                // Two 404s: no mirror at all, or a SHA (`at=`) that is not in the walk.
                return missing?.State == "not-found" ? SidecarGraphResult.NotFound() : SidecarGraphResult.Missing();
            default:
                response.EnsureSuccessStatusCode();
                throw new InvalidOperationException($"Unexpected sidecar status {(int)response.StatusCode}.");
        }
    }

    public async Task DeleteAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        // Waits for an in-flight fetch, which may run for the longest quota.
        var o = options.Value;
        var bound = TimeSpan.FromSeconds(Math.Max(o.Installed.TimeoutSeconds, o.Anonymous.TimeoutSeconds) + 60);
        using var cts = Bounded(bound, cancellationToken);
        using var response = await http.DeleteAsync($"repos/{Path(repositoryId)}", cts.Token);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SidecarStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        // The sidecar sums every mirror directory for this; a hung one must not hold a dashboard reading.
        using var cts = Bounded(StatsTimeout, cancellationToken);
        using var response = await http.GetAsync("stats", cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SidecarStats>(Json, cts.Token)
            ?? throw new InvalidOperationException("The sidecar answered /stats with an empty body.");
    }

    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(10);

    private static CancellationTokenSource Bounded(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary><c>{provider}/{repoId}</c> for a repository document id, validated against the contract's grammar.</summary>
    public static string Path(string repositoryId)
        => TryPath(repositoryId, out var path)
            ? path
            : throw new ArgumentException($"'{repositoryId}' does not name a repository the sidecar can address.", nameof(repositoryId));

    /// <summary>As <see cref="Path"/>, without throwing.</summary>
    public static bool TryPath(string? repositoryId, out string path)
    {
        path = string.Empty;
        var match = repositoryId is null ? null : RepositoryIdShape().Match(repositoryId);
        if (match is not { Success: true }) return false;
        path = $"{match.Groups["provider"].Value}/{match.Groups["repo"].Value}";
        return true;
    }

    [GeneratedRegex("^Repositories/(?<provider>[a-z]+)/(?<repo>[0-9A-Za-z_-]{1,64})$")]
    private static partial Regex RepositoryIdShape();
}

/// <summary>The body of <c>POST /repos/{provider}/{repoId}/fetch</c>. <paramref name="Mode"/> is one of <see cref="SidecarFetchModes"/>.</summary>
public sealed record SidecarFetchRequest(string CloneUrl, SidecarCredential? Credential, SidecarQuota Quota, string Mode);

/// <summary>
/// Whether a fetch may create a mirror (D10, D40). Only a view-initiated clone sends
/// <see cref="CloneOrFetch"/>; a push, a repository joining an installation, the hourly safety net and the
/// stale-on-view refresh send <see cref="FetchOnly"/>, which the sidecar answers <c>refused</c>/<c>missing</c>
/// without starting git when it has no mirror (a lost volume), instead of re-cloning a repository nobody viewed.
/// </summary>
public static class SidecarFetchModes
{
    public const string CloneOrFetch = "clone-or-fetch";
    public const string FetchOnly = "fetch-only";
}

/// <summary>A forge credential, sent once and never stored.</summary>
public sealed class SidecarCredential(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;

    /// <summary>Redacted, so a log line or an assertion message can never carry it.</summary>
    public override string ToString() => $"SidecarCredential {{ Username = {Username} }}";
}

/// <summary>The quota the sidecar enforces for one fetch.</summary>
public sealed record SidecarQuota(long MaxBytes, int MaxRefs, int TimeoutSeconds);

/// <summary>The sidecar's answer to a fetch.</summary>
public sealed record SidecarFetchResult(
    string Outcome, string? Reason, long SizeBytes, string? RefsHash, long CommitCount, long FreeBytes, long DurationMs)
{
    /// <summary>The mirror exists after this fetch.</summary>
    public bool Succeeded => Outcome is "cloned" or "fetched";
}

/// <summary>The graph read's query string parameters (contract §GET graph).</summary>
public sealed record SidecarGraphQuery(
    int From, int Count, string? Ref, string? At, string? Snapshot, bool FirstParent, string? Grep, string? Author)
{
    public string ToQueryString()
    {
        var query = new Dictionary<string, string?>
        {
            ["from"] = From.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["count"] = Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ref"] = Ref,
            ["at"] = At,
            ["snapshot"] = Snapshot,
            ["firstParent"] = FirstParent ? "true" : null,
            ["grep"] = Grep,
            ["author"] = Author,
        };
        return Microsoft.AspNetCore.Http.QueryString.Create(
            query.Where(p => !string.IsNullOrEmpty(p.Value))).ToUriComponent();
    }
}

/// <summary>A page of rows (contract §GET graph, 200).</summary>
public sealed record SidecarGraphPage(
    string SnapshotId, string? RefsHash, int TotalRows, int From, int LaneCount,
    int[][] Top, IReadOnlyList<SidecarRow> Rows, IReadOnlyList<SidecarRef> Refs);

/// <summary>
/// One commit as the sidecar lays it out.
/// ⚠️ <see cref="AuthorEmail"/> is for avatar resolution on the server only and never reaches a browser (D28).
/// </summary>
public sealed record SidecarRow(
    string Sha, string[] Parents, string AuthorName, string? AuthorEmail, DateTimeOffset Date, string Subject,
    int Lane, int Colour, int[] Up, int[][] Down);

/// <summary>A branch or tag.</summary>
public sealed record SidecarRef(string Name, string Kind, string Sha, string? Run = null);

/// <summary>The body of <c>GET /stats</c>: the mirror volume as the sidecar sees it, and the mirrors on it.</summary>
public sealed record SidecarStats(long FreeBytes, long TotalBytes, int MirrorCount, long MirrorBytes);

/// <summary>The body of a 404 or 410.</summary>
public sealed record SidecarStateBody(string? State, string? SnapshotId);

/// <summary>A graph read's outcome.</summary>
public sealed record SidecarGraphResult(SidecarGraphStatus Status, SidecarGraphPage? Page, string? SnapshotId)
{
    public static SidecarGraphResult Ok(SidecarGraphPage page) => new(SidecarGraphStatus.Ok, page, page.SnapshotId);
    public static SidecarGraphResult Missing() => new(SidecarGraphStatus.Missing, null, null);
    public static SidecarGraphResult Stale(string? snapshotId) => new(SidecarGraphStatus.Stale, null, snapshotId);
    public static SidecarGraphResult NotFound() => new(SidecarGraphStatus.NotFound, null, null);
}

public enum SidecarGraphStatus
{
    /// <summary>A page.</summary>
    Ok,

    /// <summary>No mirror exists.</summary>
    Missing,

    /// <summary>The snapshot the client holds is no longer current (D21).</summary>
    Stale,

    /// <summary>The <c>at=</c> SHA is not in the walk.</summary>
    NotFound,
}
