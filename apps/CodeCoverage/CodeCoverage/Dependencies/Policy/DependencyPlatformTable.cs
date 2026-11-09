using System.Globalization;
using System.Text.Json;
using CodeCoverage.Entities;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// #606 D9: the channel table the bot passes to every lookup as its required <c>platforms</c> input, so every repository is
/// offered SDK moves from the same facts and the action fetches no feed. Built from the <see cref="PlatformRelease"/> rows
/// of the SDKs the updater moves; end-of-life channels are left out (a lookup never offers one).
/// </summary>
/// <remarks>
/// The shape is <c>{"sdks":{"&lt;sdk&gt;":[candidate…]},"unstable":[glob…]}</c>, validated by the action's
/// <c>parsePlatformTable</c>. A candidate's <c>latestSdk</c> is .NET's SDK (global.json pins an SDK, not a runtime) or a
/// Rust beta's dated toolchain (<c>beta-2026-11-08</c>, D11), which the plan writes to rust-toolchain.toml.
/// <c>lts</c> is null for an SDK without LTS releases, so a <c>track: lts</c> there falls back to stable (D8).
/// </remarks>
public static class DependencyPlatformTable
{
    /// <summary>The table from every collected channel (a few dozen documents, read by id prefix: no index to wait for).</summary>
    public static async Task<string> JsonAsync(IAsyncDocumentSession session, IReadOnlyList<string> unstable, CancellationToken ct)
        => Json(await session.Advanced.LoadStartingWithAsync<PlatformRelease>(PlatformRelease.Prefix, pageSize: 1024, token: ct), unstable);

    public static string Json(IEnumerable<PlatformRelease> releases, IReadOnlyList<string> unstable)
    {
        var sdks = new SortedDictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            if (release.Phase == "eol" || !DependenciesYml.PlatformSdks.Contains(release.Platform, StringComparer.Ordinal))
                continue;
            if (!sdks.TryGetValue(release.Platform, out var list))
                sdks[release.Platform] = list = [];
            var preview = release.Phase == "preview";
            list.Add(new()
            {
                ["channel"] = release.Channel,
                ["latestVersion"] = release.LatestVersion,
                ["latestSdk"] = release.LatestSdk ?? (release.Platform == "rust" && preview ? release.Name : null),
                ["releaseTimestamp"] = Date(release.LatestReleasedOn ?? release.ReleasedOn),
                ["preview"] = preview,
                ["eol"] = Date(release.SupportEndsOn),
                ["lts"] = DependenciesYml.LtsSdks.Contains(release.Platform, StringComparer.Ordinal) ? release.IsLts : null,
            });
        }
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sdks"] = sdks,
            ["unstable"] = unstable,
        });
    }

    private static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
