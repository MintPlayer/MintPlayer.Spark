using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Mcp;

/// <summary>
/// The coverage numbers the MCP tools answer with (MCP PRD §4.8), read for a repository the caller may already see.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Every method takes a <see cref="Repository"/> the caller resolved through <see cref="IRepositoryScope"/></b>
/// (<see cref="CoverageTools"/> does so first, and refuses alike when it answers null). Nothing here decides who may see
/// a repository; it reads below that gate, the way the browse API's actions read below theirs. Commits are reached by
/// the repository's id, so no answer can name another repository's data.
/// </para>
/// <para>
/// A service rather than code in the tool class: a tool class holds no raw session (analyzer SPARK050). The per-file
/// documents (<see cref="BuildTreeSummary"/>, <see cref="FileCoverage"/>) are not Spark entity types and are exposed only
/// through these reads, as PRD §4.8.2 decides.
/// </para>
/// </remarks>
public interface ICoverageInsights
{
    /// <summary>
    /// The commit a tool reads: <paramref name="sha"/> when given; otherwise, on the default branch, the commit the
    /// repository's headline (badge, repository page) shows; otherwise the branch's newest commit with complete
    /// coverage, else its newest with any. <see langword="null"/> when there is no such commit with coverage.
    /// </summary>
    Task<Commit?> PickCommitAsync(Repository repository, string? branch, string? sha, CancellationToken cancellationToken = default);

    /// <summary>Totals, the trend over the last <see cref="TrendLength"/> commits with coverage, and the largest drops among them.</summary>
    Task<CoverageOverview> SummarizeAsync(Repository repository, string? branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// The commit's files ranked by uncovered lines, most first, ties by path (spike S8: no "ease" factor). The covered
    /// percentage and the number of uncovered line ranges are columns, never part of the order.
    /// </summary>
    Task<HotspotPage> HotspotsAsync(Commit commit, string? pathPrefix, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>One file's uncovered line ranges and partly covered branches at the commit; <see langword="null"/> when the commit measured no such file.</summary>
    Task<FileReport?> FileAsync(Commit commit, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The pull request's patch coverage and its uncovered added lines, from the newest head commit with coverage;
    /// <see langword="null"/> when no commit of the pull request has coverage.
    /// </summary>
    Task<PatchReport?> PatchAsync(Repository repository, int pullRequest, CancellationToken cancellationToken = default);

    /// <summary>Commits that lowered coverage against their parent, newest first, each with the files whose uncovered lines grew most.</summary>
    Task<IReadOnlyList<CoverageDropReport>> DropsAsync(Repository repository, string? branch, DateTimeOffset? since, int take, CancellationToken cancellationToken = default);
}

/// <summary>A commit as the tools name it.</summary>
public sealed record CommitRef(string Sha, string? Branch, DateTimeOffset? Date, double? LinePercent, double? BranchPercent, string? Completeness);

/// <summary>What <c>codecoverage_summary</c> answers.</summary>
public sealed record CoverageOverview(
    string? Branch, CommitRef? Latest, CoverageSummary? Totals, IReadOnlyList<TrendPoint> Trend, IReadOnlyList<TrendDrop> LargestDrops, IReadOnlyList<string> Branches);

/// <summary>One point of the trend, oldest first.</summary>
public sealed record TrendPoint(string Sha, DateTimeOffset? Date, double Percent);

/// <summary>A commit of the trend whose coverage fell against its parent.</summary>
public sealed record TrendDrop(string Sha, DateTimeOffset? Date, double Percent, double DeltaVsParent, string? Message);

/// <summary>One page of hotspots and how many files have uncovered lines in all.</summary>
public sealed record HotspotPage(int TotalFiles, IReadOnlyList<Hotspot> Files);

/// <summary>One file with uncovered lines. <paramref name="UncoveredRanges"/> is null when the file's line data is missing.</summary>
public sealed record Hotspot(string Path, int UncoveredLines, int CoverableLines, double CoveredPercent, int? UncoveredRanges);

/// <summary>What <c>codecoverage_file</c> answers. Line numbers only, never source.</summary>
public sealed record FileReport(
    string Path, int CoverableLines, int CoveredLines, int UncoveredLines,
    IReadOnlyList<string> UncoveredRanges, int OmittedRanges,
    IReadOnlyList<PartialBranch> PartialBranches, int OmittedPartialBranches,
    int BranchesCovered, int BranchesTotal);

/// <summary>A line with some branch arms never taken.</summary>
public sealed record PartialBranch(int Line, int Covered, int Total);

/// <summary>What <c>codecoverage_patch</c> answers.</summary>
/// <param name="Files">Files with uncovered added lines; <see langword="null"/> when the diff could not be read from the forge.</param>
public sealed record PatchReport(int PullRequest, string HeadSha, string? Branch, PatchCoverage? Patch, IReadOnlyList<PatchFile>? Files, int OmittedFiles);

/// <summary>A changed file's uncovered added lines.</summary>
public sealed record PatchFile(string Path, int UncoveredLines, IReadOnlyList<string> Ranges, int OmittedRanges);

/// <summary>One commit that lowered coverage.</summary>
/// <param name="Files">The files whose uncovered lines grew most; <see langword="null"/> when the parent's coverage is unknown.</param>
public sealed record CoverageDropReport(
    string Sha, string? Branch, DateTimeOffset? Date, string? Message, double? Percent, double DeltaVsParent, string? ParentSha, IReadOnlyList<FileChange>? Files);

/// <summary>A file's uncovered lines before and after a commit.</summary>
public sealed record FileChange(string Path, int UncoveredBefore, int UncoveredAfter);

/// <inheritdoc cref="ICoverageInsights"/>
[Register(typeof(ICoverageInsights), ServiceLifetime.Scoped)]
public partial class CoverageInsights : ICoverageInsights
{
    /// <summary>How many commits the trend covers (PRD §4.8: "30-commit trend").</summary>
    public const int TrendLength = 30;

    /// <summary>How many of the trend's drops <c>summary</c> names.</summary>
    public const int LargestDropCount = 5;

    /// <summary>Bounds that keep every answer below the output cap (D10) whatever the repository looks like.</summary>
    public const int MaxRanges = 400;
    public const int MaxPartialBranches = 200;
    public const int MaxPatchFiles = 30;
    public const int MaxPatchRanges = 40;
    public const int MaxFilesPerDrop = 5;
    private const int MaxMessageLength = 120;

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IRepositoryScope scope;
    [Inject] private readonly IForgeIntegrationResolver forges;

    public async Task<Commit?> PickCommitAsync(Repository repository, string? branch, string? sha, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(sha))
        {
            var named = await session.LoadAsync<Commit>(Commit.DocumentId(repository.Provider, repository.GitHubId, sha), cancellationToken);
            return named?.LatestBuildId is null ? null : named;
        }

        var effectiveBranch = string.IsNullOrEmpty(branch) ? repository.DefaultBranch : branch;

        // The default branch reads what the website's headline shows, so an agent and the badge agree.
        if (repository.LatestCoverageSha is { } headline
            && (effectiveBranch is null || string.Equals(effectiveBranch, repository.DefaultBranch, StringComparison.Ordinal)))
        {
            var shown = await session.LoadAsync<Commit>(Commit.DocumentId(repository.Provider, repository.GitHubId, headline), cancellationToken);
            if (shown?.LatestBuildId is not null)
                return shown;
        }

        // DefaultBranch is null for repositories provisioned by an OIDC upload; like the history chart, fall back to
        // every branch rather than to nothing.
        var covered = scope.Commits(repository).Where(c => c.HasCoverage);
        if (!string.IsNullOrEmpty(effectiveBranch))
            covered = covered.Where(c => c.Branch == effectiveBranch);

        return await covered.Where(c => c.CompleteCoverage)
                   .OrderByDescending(c => c.Date).OfType<Commit>().FirstOrDefaultAsync(cancellationToken)
               ?? await covered
                   .OrderByDescending(c => c.Date).OfType<Commit>().FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<CoverageOverview> SummarizeAsync(Repository repository, string? branch, CancellationToken cancellationToken = default)
    {
        var effectiveBranch = string.IsNullOrEmpty(branch) ? repository.DefaultBranch : branch;
        var latest = await PickCommitAsync(repository, branch, sha: null, cancellationToken);

        var query = scope.Commits(repository).Where(c => c.HasCoverage);
        if (!string.IsNullOrEmpty(effectiveBranch))
            query = query.Where(c => c.Branch == effectiveBranch);

        var recent = await query
            .OrderByDescending(c => c.Date)
            .Take(TrendLength)
            .OfType<Commit>()
            .ToListAsync(cancellationToken);

        var trend = recent
            .Where(c => c.Coverage is { LinesCoverable: > 0 })
            .Reverse()
            .Select(c => new TrendPoint(c.Sha, c.Date, Percent(c.Coverage!.LinesCovered, c.Coverage.LinesCoverable)))
            .ToList();

        var drops = recent
            .Where(c => c.CoverageDeltaVsParent is < 0 && c.Coverage is { LinesCoverable: > 0 })
            .OrderBy(c => c.CoverageDeltaVsParent)
            .Take(LargestDropCount)
            .Select(c => new TrendDrop(c.Sha, c.Date, Percent(c.Coverage!.LinesCovered, c.Coverage.LinesCoverable),
                Math.Round(c.CoverageDeltaVsParent!.Value, 2), FirstLine(c.Message)))
            .ToList();

        var branches = await scope.ListBranchesAsync(repository, cancellationToken);

        return new CoverageOverview(
            effectiveBranch,
            latest is null ? null : Ref(latest),
            latest?.Coverage,
            trend,
            drops,
            [.. branches.Take(20)]);
    }

    public async Task<HotspotPage> HotspotsAsync(Commit commit, string? pathPrefix, int skip, int take, CancellationToken cancellationToken = default)
    {
        var source = await CoverageSources.ForCommitAsync(session, commit, cancellationToken);
        var prefix = NormalizePrefix(pathPrefix);

        var ranked = (await CoverageSources.LoadTreeAsync(session, source, cancellationToken))
            .Where(f => f.Matched && f.LinesCoverable > 0 && f.LinesCoverable > f.LinesCovered)
            .Where(f => prefix.Length == 0 || f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(f => f.LinesCoverable - f.LinesCovered)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var page = ranked.Skip(skip).Take(take).ToList();

        // The ranges need each file's line data: loaded for this page only, in one round trip.
        Dictionary<string, FileCoverage> documents = page.Count == 0
            ? []
            : await session.LoadAsync<FileCoverage>(page.Select(f => FileCoverage.DocumentId(source, f.Path)), cancellationToken);

        return new HotspotPage(ranked.Count, [.. page.Select(f => new Hotspot(
            f.Path,
            f.LinesCoverable - f.LinesCovered,
            f.LinesCoverable,
            Percent(f.LinesCovered, f.LinesCoverable),
            documents.GetValueOrDefault(FileCoverage.DocumentId(source, f.Path)) is { } file ? LineRanges.Uncovered(file.Lines).Count : null))]);
    }

    public async Task<FileReport?> FileAsync(Commit commit, string path, CancellationToken cancellationToken = default)
    {
        var source = await CoverageSources.ForCommitAsync(session, commit, cancellationToken);
        var file = await session.LoadAsync<FileCoverage>(FileCoverage.DocumentId(source, NormalizePath(path)), cancellationToken);
        if (file is null)
            return null;

        var lines = LineRanges.Distinct(file.Lines);
        var ranges = LineRanges.Uncovered(lines);
        var partial = file.Branches.Where(b => b.IsPartial).OrderBy(b => b.Line).ToList();
        var covered = lines.Count(l => l.Status != LineStatus.NotCovered);

        return new FileReport(
            file.Path,
            lines.Count,
            covered,
            lines.Count - covered,
            [.. ranges.Take(MaxRanges).Select(LineRanges.Format)],
            Math.Max(0, ranges.Count - MaxRanges),
            [.. partial.Take(MaxPartialBranches).Select(b => new PartialBranch(b.Line, b.Covered, b.Arity))],
            Math.Max(0, partial.Count - MaxPartialBranches),
            file.Branches.Sum(b => b.Covered),
            file.Branches.Sum(b => b.Arity));
    }

    public async Task<PatchReport?> PatchAsync(Repository repository, int pullRequest, CancellationToken cancellationToken = default)
    {
        // Fork contributions included, unlike every repository-level listing: a pull request from a fork has only those
        // commits, and its pull request is exactly the context in which they mean something. Still this repository's
        // own id, so nothing of another repository is reachable.
        var head = await session.Query<Indexes.VCommit, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id && c.PullRequestNumber == pullRequest && c.HasCoverage)
            .OrderByDescending(c => c.Date)
            .OfType<Commit>()
            .FirstOrDefaultAsync(cancellationToken);
        if (head?.LatestBuildId is null)
            return null;

        var build = await session.LoadAsync<Build>(head.LatestBuildId, cancellationToken);
        var patch = build?.Patch;
        if (build?.Id is null || patch?.DiffBaseRef is null)
            return new PatchReport(pullRequest, head.Sha, head.Branch, patch, Files: null, OmittedFiles: 0);

        // The same diff and the same matching the patch number was computed with (PatchCoverageCalculator): GitHub's
        // three-dot comparison (cached for minutes), the build's own per-file documents, partial lines counting as hits.
        var comparison = await forges.For(repository).CompareAsync(repository, patch.DiffBaseRef, head.Sha, cancellationToken);
        if (comparison is null)
            return new PatchReport(pullRequest, head.Sha, head.Branch, patch, Files: null, OmittedFiles: 0);

        var candidates = comparison.Files
            .Where(f => f.Status != "removed" && f.AddedLines.Length > 0)
            .GroupBy(f => FileCoverage.DocumentId(build.Id, f.Path))
            .ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, FileCoverage> measured = candidates.Count == 0
            ? []
            : await session.LoadAsync<FileCoverage>(candidates.Keys, cancellationToken);

        var files = new List<PatchFile>();
        foreach (var (id, diffFile) in candidates.OrderBy(c => c.Value.Path, StringComparer.Ordinal))
        {
            if (measured.GetValueOrDefault(id) is not { } file)
                continue; // in the diff, not in the report: skipped, not zeroed, as the patch number does

            var byNumber = LineRanges.Distinct(file.Lines).ToDictionary(l => l.Number);
            var uncovered = diffFile.AddedLines
                .Where(n => byNumber.TryGetValue(n, out var line) && line.Status == LineStatus.NotCovered)
                .ToList();
            if (uncovered.Count == 0)
                continue;

            var ranges = LineRanges.Consecutive(uncovered);
            files.Add(new PatchFile(diffFile.Path, uncovered.Count,
                [.. ranges.Take(MaxPatchRanges).Select(LineRanges.Format)], Math.Max(0, ranges.Count - MaxPatchRanges)));
        }

        var ordered = files.OrderByDescending(f => f.UncoveredLines).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new PatchReport(pullRequest, head.Sha, head.Branch, patch,
            [.. ordered.Take(MaxPatchFiles)], Math.Max(0, ordered.Count - MaxPatchFiles));
    }

    public async Task<IReadOnlyList<CoverageDropReport>> DropsAsync(Repository repository, string? branch, DateTimeOffset? since, int take, CancellationToken cancellationToken = default)
    {
        var query = scope.Commits(repository).Where(c => c.CoverageDeltaVsParent < 0d);
        if (!string.IsNullOrEmpty(branch))
            query = query.Where(c => c.Branch == branch);

        // The date bound is applied here rather than in the query: the index orders by Date already, and a range filter
        // on a DateTimeOffset field is not something this index was built for.
        var candidates = await query
            .OrderByDescending(c => c.Date)
            .Take(200)
            .OfType<Commit>()
            .ToListAsync(cancellationToken);

        var drops = candidates
            .Where(c => since is null || (c.Date is { } date && date >= since))
            .Take(take)
            .ToList();

        var reports = new List<CoverageDropReport>(drops.Count);
        foreach (var commit in drops)
        {
            reports.Add(new CoverageDropReport(
                commit.Sha,
                commit.Branch,
                commit.Date,
                FirstLine(commit.Message),
                commit.Coverage is { LinesCoverable: > 0 } s ? Percent(s.LinesCovered, s.LinesCoverable) : null,
                Math.Round(commit.CoverageDeltaVsParent ?? 0, 2),
                commit.ParentSha,
                await ResponsibleFilesAsync(repository, commit, cancellationToken)));
        }
        return reports;
    }

    /// <summary>The files whose uncovered lines grew most between the commit's parent and the commit.</summary>
    private async Task<IReadOnlyList<FileChange>?> ResponsibleFilesAsync(Repository repository, Commit commit, CancellationToken cancellationToken)
    {
        if (commit.LatestBuildId is null || string.IsNullOrEmpty(commit.ParentSha))
            return null;

        var parent = await session.LoadAsync<Commit>(Commit.DocumentId(repository.Provider, repository.GitHubId, commit.ParentSha), cancellationToken);
        if (parent?.LatestBuildId is null)
            return null;

        var after = await CoverageSources.LoadTreeAsync(session, await CoverageSources.ForCommitAsync(session, commit, cancellationToken), cancellationToken);
        var before = (await CoverageSources.LoadTreeAsync(session, await CoverageSources.ForCommitAsync(session, parent, cancellationToken), cancellationToken))
            .Where(f => f.Matched)
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().LinesCoverable - g.First().LinesCovered, StringComparer.Ordinal);

        return [.. after
            .Where(f => f.Matched)
            .Select(f => new FileChange(f.Path, before.GetValueOrDefault(f.Path), f.LinesCoverable - f.LinesCovered))
            .Where(f => f.UncoveredAfter > f.UncoveredBefore)
            .OrderByDescending(f => f.UncoveredAfter - f.UncoveredBefore)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(MaxFilesPerDrop)];
    }

    private static CommitRef Ref(Commit commit) => new(
        commit.Sha,
        commit.Branch,
        commit.Date,
        commit.Coverage is { LinesCoverable: > 0 } lines ? Percent(lines.LinesCovered, lines.LinesCoverable) : null,
        commit.Coverage is { BranchesTotal: > 0 } arms ? Percent(arms.BranchesCovered, arms.BranchesTotal) : null,
        commit.AssemblyCompleteness);

    /// <summary>Covered over coverable × 100, to one decimal, as the website rounds it.</summary>
    internal static double Percent(int covered, int coverable)
        => coverable == 0 ? 0 : Math.Round(covered * 100.0 / coverable, 1);

    /// <summary>A commit message's first line, cut short: enough to recognise it, too little to carry much else.</summary>
    private static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var line = message.Split('\n', 2)[0].TrimEnd('\r').Trim();
        return line.Length <= MaxMessageLength ? line : line[..MaxMessageLength] + "…";
    }

    /// <summary>A repository-relative path as the reports store it: forward slashes, no leading <c>./</c> or <c>/</c>.</summary>
    internal static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static string NormalizePrefix(string? prefix)
        => string.IsNullOrWhiteSpace(prefix) ? string.Empty : NormalizePath(prefix);
}

/// <summary>Line-number ranges, by spike S8's rule.</summary>
internal static class LineRanges
{
    /// <summary>One entry per line number (the first, as the patch calculation takes it), in line order.</summary>
    public static List<LineCoverage> Distinct(IEnumerable<LineCoverage> lines)
        => [.. lines.GroupBy(l => l.Number).Select(g => g.First()).OrderBy(l => l.Number)];

    /// <summary>
    /// Runs of uncovered coverable lines: a line the report does not list (blank, comment) does not split a run, and a
    /// partly covered line counts as covered. This is the "uncovered line runs" count spike S8 measured.
    /// </summary>
    public static List<(int First, int Last)> Uncovered(IEnumerable<LineCoverage> lines)
    {
        var ranges = new List<(int First, int Last)>();
        int? first = null, last = null;
        foreach (var line in Distinct(lines))
        {
            if (line.Status == LineStatus.NotCovered)
            {
                first ??= line.Number;
                last = line.Number;
            }
            else if (first is { } start)
            {
                ranges.Add((start, last!.Value));
                first = last = null;
            }
        }
        if (first is { } open)
            ranges.Add((open, last!.Value));
        return ranges;
    }

    /// <summary>Consecutive numbers merged: 3, 4, 5, 9 → 3-5, 9.</summary>
    public static List<(int First, int Last)> Consecutive(IEnumerable<int> numbers)
    {
        var ranges = new List<(int First, int Last)>();
        foreach (var number in numbers.Distinct().Order())
        {
            if (ranges.Count > 0 && ranges[^1].Last == number - 1)
                ranges[^1] = (ranges[^1].First, number);
            else
                ranges.Add((number, number));
        }
        return ranges;
    }

    public static string Format((int First, int Last) range)
        => range.First == range.Last ? range.First.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{range.First.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{range.Last.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
