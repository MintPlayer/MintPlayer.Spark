using System.Globalization;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// #606 D9: the channel table the bot passes to every lookup as its required <c>platforms</c> input, so every repository is
/// offered SDK moves from the same facts and the action fetches no feed. Built from the <see cref="PlatformRelease"/> rows
/// of the SDKs the updater moves; end-of-life channels are left out (a lookup never offers one).
/// </summary>
/// <remarks>
/// The shape is <c>{"sdks":{"&lt;sdk&gt;":[candidate…]},"unstable":[glob…],"stable":[glob…]}</c>, validated by the action's
/// <c>parsePlatformTable</c>. A candidate's <c>latestSdk</c> is .NET's SDK (global.json pins an SDK, not a runtime) or a
/// Rust beta's dated toolchain (<c>beta-2026-11-08</c>, D11), which the plan writes to rust-toolchain.toml.
/// <c>lts</c> is null for an SDK without LTS releases, so a <c>track: lts</c> there falls back to stable (D8).
/// </remarks>
public static partial class DependencyPlatformTable
{
    /// <summary>The table from every collected channel (a few dozen documents, read by id prefix: no index to wait for).</summary>
    public static async Task<string> JsonAsync(IAsyncDocumentSession session, IReadOnlyList<string> unstable, IReadOnlyList<string> stable, CancellationToken ct,
        IReadOnlyList<DependencyFollowRule>? follow = null)
        => Json(await session.Advanced.LoadStartingWithAsync<PlatformRelease>(PlatformRelease.Prefix, pageSize: 1024, token: ct), unstable, stable, follow);

    public static string Json(IEnumerable<PlatformRelease> releases, IReadOnlyList<string> unstable, IReadOnlyList<string>? stable = null,
        IReadOnlyList<DependencyFollowRule>? follow = null)
    {
        var sdks = new SortedDictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            // #613 item 1: a row the lookup's parsePlatformTable (feeds.ts) would refuse fails EVERY lookup, so it never goes
            // in: the per-major rows written before #606 had no channel ("channel":"" failed 10 of 10 first lookups on
            // 2026-11-09, e.g. MintPlayer/mintplayer-ng-seo run 42650847860).
            if (release.Phase == "eol" || !DependenciesYml.PlatformSdks.Contains(release.Platform, StringComparer.Ordinal) || !IsChannel(release.Channel))
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
            // prerelease-dependencies-PRD H5: looked up stable-only even when already on a prerelease (`track: stable`).
            ["stable"] = stable ?? [],
            // dependency-companion-pins-PRD F2: coverage.yml's follow rules; the lookup emits a record per match.
            ["follow"] = (follow ?? []).Select(r => new Dictionary<string, string?>
            {
                ["dependency"] = r.Dependency,
                ["path"] = r.Path,
                ["match"] = r.Match,
                ["version"] = r.Version,
            }).ToList(),
        });
    }

    /// <summary>A channel the action accepts: <c>^\d{1,4}(\.\d{1,4})?$</c> (dependency-action/src/platform/feeds.ts).</summary>
    public static bool IsChannel(string? channel) => channel is not null && ChannelPattern().IsMatch(channel);

    [System.Text.RegularExpressions.GeneratedRegex(@"\A\d{1,4}(\.\d{1,4})?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ChannelPattern();

    private static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
