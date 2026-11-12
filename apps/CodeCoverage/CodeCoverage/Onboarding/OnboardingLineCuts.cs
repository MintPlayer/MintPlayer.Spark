using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Onboarding;

/// <summary>A release line the onboarding creates (docs/onboarding_line_cuts_PRD.md D1, D2).</summary>
/// <param name="Branch">The line's branch, e.g. <c>release/dotnet-11.x</c>.</param>
/// <param name="TargetFramework">Null for a current line; for an offered older line, the target framework that offers it.</param>
/// <param name="Packages">The packages that put it there.</param>
public sealed record OnboardingLineCut(string Branch, string? TargetFramework, IReadOnlyList<string> Packages)
{
    /// <summary>Why the line is created, for the plan: "the current line" or "still targets net10.0".</summary>
    public string Reason => TargetFramework is null ? "the current line" : $"still targets {TargetFramework}";
}

/// <summary>
/// The release lines an onboarding with release lines creates (the Tools onboarding, 2026-11-12: it renamed the old lines
/// but left Tools without <c>release/dotnet-11.x</c>, because the versioning run cuts only a major nothing is tagged at
/// yet, <see cref="ReleaseBranchCuts.ForLinesAsync"/>).
/// </summary>
/// <remarks>
/// <para><b>Current</b> (D1): the line of every anchored package, as <see cref="ReleasePlanner.LinesOf"/> derives it,
/// that has no branch. A package anchored by <c>versioning.packages.&lt;id&gt;.anchor</c> is left to the versioning run:
/// its major needs the build's resolved versions, which the planner does not have.</para>
/// <para><b>Older</b> (D2): a lower <c>netX.0</c> a .NET package still targets beside its anchor (<c>net10.0</c> in
/// <c>net10.0;net11.0</c>). It is only offered: the owner picks it, since a multi-target may be compatibility rather than
/// support. Nothing else is guessed.</para>
/// </remarks>
internal static class OnboardingLineCuts
{
    public static (IReadOnlyList<OnboardingLineCut> Current, IReadOnlyList<OnboardingLineCut> Older) Plan(ProjectGraph graph, VersioningSettings settings,
        ReleaseLinePattern pattern, IReadOnlyCollection<string> branches)
    {
        var existing = branches.ToHashSet(StringComparer.Ordinal);
        var current = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var older = new SortedDictionary<string, (string TargetFramework, List<string> Packages)>(StringComparer.Ordinal);
        foreach (var package in graph.Packages)
        {
            if (settings.Anchors.ContainsKey(package.Id!)) continue;
            MajorAnchor anchor;
            try { anchor = graph.AnchorOf(package); }
            catch (VersioningException) { continue; }
            if (anchor.Major is not { } major || Ecosystems.Of(graph, package, settings.LinePins) is not { } ecosystem) continue;

            var line = pattern.Format(new ReleaseLine(ecosystem, major));
            if (!current.TryGetValue(line, out var ids)) current[line] = ids = [];
            ids.Add(package.Id!);
            if (package.Kind != PackageKind.NuGet) continue;

            foreach (var tfm in package.TargetFrameworks)
            {
                int? lower;
                try { lower = MajorAnchor.FromTargetFrameworks([tfm]).Major; }
                catch (VersioningException) { continue; }
                if (lower is not { } m || m >= major) continue;
                var branch = pattern.Format(new ReleaseLine(ecosystem, m));
                if (!older.TryGetValue(branch, out var offered)) older[branch] = offered = ($"net{m}.0", []);
                if (!offered.Packages.Contains(package.Id!)) offered.Packages.Add(package.Id!);
            }
        }

        return (
            [.. current.Where(c => !existing.Contains(c.Key)).Select(c => new OnboardingLineCut(c.Key, null, [.. c.Value.Order(StringComparer.Ordinal)]))],
            [.. older.Where(o => !existing.Contains(o.Key) && !current.ContainsKey(o.Key))
                .Select(o => new OnboardingLineCut(o.Key, o.Value.TargetFramework, [.. o.Value.Packages.Order(StringComparer.Ordinal)]))]);
    }
}
