using CodeCoverage.Entities;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.PackageVersioning;

/// <summary>One existing release line and whether it is supported (#570 M8, PRD D4, D16).</summary>
/// <param name="Current">The newest line of its ecosystem: the one the default branch's major releases on (D15).</param>
/// <param name="Reason">Why, for people: shown on the "Backport targets" check and in refusals.</param>
public sealed record LineSupport(ReleaseLine Line, string Branch, bool Supported, bool Current, string Reason);

/// <summary>
/// Which release lines are supported (#570 M8, PRD D4): the lines that exist (the branches the line pattern matches),
/// judged by the platform lifecycle the app collects (<see cref="PlatformRelease"/>), with the owner's
/// <c>backport.lines</c> override per line.
/// </summary>
/// <remarks>
/// In order: <c>retire</c> drops a line, <c>keep</c> keeps it; the newest line of an ecosystem is always supported (D16:
/// the current line receives every change by backport); then the platform's phase decides, anything but <c>eol</c>
/// being supported. An ecosystem with no lifecycle data at all has no support policy and falls back to the explicit
/// list (D4): only its <c>keep</c> lines and its current line. A major missing from data the ecosystem does have is
/// treated the same, since nothing says it is maintained.
/// </remarks>
public static class SupportedLines
{
    public const string Keep = "keep";
    public const string Retire = "retire";

    /// <summary>The reason a current line with <c>versioning.lines.current: promote</c> is not backported to (segment P, P0).</summary>
    public const string PromotedReason = "promoted with `/release`, so it takes no backports";

    /// <param name="promotesCurrent"><c>versioning.lines.current: promote</c> (segment P, P0): every current line is then
    /// unsupported here, whatever <c>backport.lines</c> says. Its changes arrive by promotion, so a backport, a default
    /// label, a per-line dependency update or a line workflow pull request would only be a line-only change that blocks
    /// the next promotion (P1).</param>
    public static IReadOnlyList<LineSupport> Evaluate(ReleaseLinePattern pattern, IEnumerable<string> branches, IReadOnlyCollection<PlatformRelease> releases,
        IReadOnlyDictionary<string, string> overrides, bool promotesCurrent = false)
    {
        var lines = new Dictionary<ReleaseLine, string>();
        foreach (var branch in branches)
            if (pattern.TryMatch(branch, out var line) && line is not null)
                lines.TryAdd(line, branch);

        var current = lines.Keys.GroupBy(l => l.Ecosystem, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Max(l => l.Major), StringComparer.Ordinal);
        var known = releases.Select(r => r.Platform).ToHashSet(StringComparer.Ordinal);

        return [.. lines
            .OrderBy(l => l.Key.Ecosystem, StringComparer.Ordinal).ThenByDescending(l => l.Key.Major)
            .Select(pair =>
            {
                var (line, branch) = (pair.Key, pair.Value);
                var isCurrent = current[line.Ecosystem] == line.Major;
                if (isCurrent && promotesCurrent) return new LineSupport(line, branch, false, true, PromotedReason);
                overrides.TryGetValue(line.ToString(), out var setting);
                if (setting == Retire) return new LineSupport(line, branch, false, isCurrent, $"`{Retire}` in coverage.yml");
                if (setting == Keep) return new LineSupport(line, branch, true, isCurrent, $"`{Keep}` in coverage.yml");
                if (isCurrent) return new LineSupport(line, branch, true, true, $"the current {line.Ecosystem} line");
                if (!known.Contains(line.Ecosystem))
                    return new LineSupport(line, branch, false, false, $"{line.Ecosystem} has no lifecycle data; `{Keep}` the line to backport to it");
                // #606: rows are per channel; a major with several (Python 3.13, 3.14) is judged by its newest.
                var release = releases.Where(r => r.Platform == line.Ecosystem && r.Major == line.Major)
                    .OrderByDescending(r => r.Channel, PlatformLifecycle.PlatformLifecycleSources.ChannelComparer.Instance).FirstOrDefault();
                return release switch
                {
                    null => new LineSupport(line, branch, false, false, $"{line.Ecosystem} {line.Major} is not in the lifecycle data; `{Keep}` the line to backport to it"),
                    { Phase: "eol" } => new LineSupport(line, branch, false, false, $"{line.Ecosystem} {line.Major} reached end of life"),
                    _ => new LineSupport(line, branch, true, false, $"{line.Ecosystem} {line.Major} is in {release.Phase}"),
                };
            })];
    }

    /// <summary>
    /// Reads the lines of <paramref name="repository"/>: its branches under the pattern's fixed start, from the forge, and
    /// every collected <see cref="PlatformRelease"/> (a few dozen documents, read by id prefix: no index to wait for).
    /// </summary>
    public static async Task<IReadOnlyList<LineSupport>> ReadAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, Forge.IForgeMergeQueue forge,
        Repository repository, ReleaseLinePattern pattern, IReadOnlyDictionary<string, string> overrides, CancellationToken cancellationToken,
        bool promotesCurrent = false)
    {
        var branches = await forge.ListBranchesAsync(repository, BranchPrefix(pattern), cancellationToken);
        var releases = await session.Advanced.LoadStartingWithAsync<PlatformRelease>(PlatformRelease.Prefix, pageSize: 1024, token: cancellationToken);
        return Evaluate(pattern, branches, [.. releases], overrides, promotesCurrent);
    }

    /// <summary>Whether <paramref name="yml"/>'s <c>versioning.lines.current</c> is <c>promote</c> (segment P).</summary>
    public static bool PromotesCurrent(string? yml) => VersioningYml.Parse(yml).Config?.Lines?.PromotesCurrent == true;

    /// <summary>The fixed start of <paramref name="pattern"/>, what the forge's branch listing filters on.</summary>
    public static string BranchPrefix(ReleaseLinePattern pattern)
    {
        var brace = pattern.Text.IndexOf('{');
        return brace < 0 ? pattern.Text : pattern.Text[..brace];
    }
}
