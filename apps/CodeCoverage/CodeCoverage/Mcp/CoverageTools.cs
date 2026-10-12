using System.ComponentModel;
using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeCoverage.Mcp;

/// <summary>
/// CodeCoverage's own MCP tools (MCP PRD §4.8, plan M6), registered unprefixed and served as <c>codecoverage_*</c>.
/// All read-only (the app's agent permission is <c>Read</c>, D24).
/// </summary>
/// <remarks>
/// <para>
/// <b>Who sees what is the website's rule, from the same place.</b> Every repository is resolved through
/// <see cref="IRepositoryScope"/>, the gate the browse API uses: public repositories for everyone, private ones only for a
/// signed-in caller with forge-verified access to the owner. A repository the caller may not see, a missing one and an
/// unknown forge all answer <see cref="SparkMcpResult.Refuse"/>: an HTTP 401 with the sign-in challenge for an anonymous
/// caller, "Not found." for a signed-in one — the same for every case, so no answer says a private repository exists
/// (#453, D17).
/// </para>
/// <para>
/// <b>The agent layers.</b> Each tool also asks <see cref="IPermissionService"/> for the rights it reads (<c>Repository</c>,
/// <c>Commit</c>), so the type's <c>agentPermissions</c> and the user's grant narrow it like every generic tool (D6, D13).
/// No raw session here (analyzer SPARK050): the reads are <see cref="ICoverageInsights"/>'s, below the gate.
/// </para>
/// <para>
/// Answers go through <see cref="SparkMcpResult.Json"/>: Spark's serializer, and fenced as untrusted data, because file
/// paths, branch names and commit messages are written by whoever pushes to the repository. Every list is bounded so an
/// answer stays below the output cap.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed partial class CoverageTools
{
    private const string RepositoryFormat =
        "Name the repository as forge/owner/name, e.g. github/octo-org/octo-repo, as codecoverage_list_repositories returns it.";

    [Inject] private readonly IRepositoryScope scope;
    [Inject] private readonly ICoverageInsights insights;
    [Inject] private readonly IPermissionService permissions;

    [McpServerTool(Name = "list_repositories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Repositories with coverage that this user can see: public ones for everyone, private ones only when signed in with access to their owner. "
        + "Each with its line and branch coverage on the default branch. Pass a row's 'repository' (forge/owner/name) to the other tools.")]
    public async Task<CallToolResult> ListRepositoriesAsync(
        [Description("Only repositories whose owner/name contains this text.")] string? search = null,
        [Description("Repositories to skip, for paging (use nextSkip from the previous page).")] int skip = 0,
        [Description("Repositories to return, 1 to 50.")] int take = 25,
        CancellationToken cancellationToken = default)
    {
        if (Paging(skip, take, max: 50) is { } invalid)
            return invalid;
        if (!await AllowedAsync(cancellationToken, ("Query", nameof(Repository))))
            return SparkMcpResult.Refuse();

        var listed = await scope.ListAsync(search, cancellationToken);
        var page = listed.Skip(skip).Take(take).Select(r => new
        {
            repository = Reference(r),
            @private = r.IsPrivate,
            defaultBranch = r.DefaultBranch,
            linePercent = r.LatestCoverage is { LinesCoverable: > 0 } lines ? CoverageInsights.Percent(lines.LinesCovered, lines.LinesCoverable) : (double?)null,
            branchPercent = r.LatestCoverage is { BranchesTotal: > 0 } arms ? CoverageInsights.Percent(arms.BranchesCovered, arms.BranchesTotal) : (double?)null,
            latestCoverageSha = r.LatestCoverageSha,
        }).ToList();

        SparkMcpResult.Track(page.Count);
        return SparkMcpResult.Json(new
        {
            totalRepositories = listed.Count,
            nextSkip = skip + page.Count < listed.Count ? skip + page.Count : (int?)null,
            repositories = page,
        });
    }

    [McpServerTool(Name = "summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Coverage of one repository on a branch (default: its default branch): the latest totals, the trend over the last 30 commits with coverage "
        + "(oldest first), the largest drops among them, and the branches that have coverage.")]
    public async Task<CallToolResult> SummaryAsync(
        [Description("The repository as forge/owner/name, e.g. github/octo-org/octo-repo.")] string repository,
        [Description("The branch; the repository's default branch when omitted.")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(cancellationToken, ("Read", nameof(Repository)), ("Query", nameof(Commit))))
            return SparkMcpResult.Refuse();
        if (!TryParse(repository, out var forge, out var owner, out var name))
            return SparkMcpResult.Invalid(RepositoryFormat);
        if (await scope.ResolveAsync(forge, owner, name, cancellationToken) is not { } resolved)
            return SparkMcpResult.Refuse();

        var overview = await insights.SummarizeAsync(resolved, branch, cancellationToken);
        SparkMcpResult.Track(overview.Trend.Count);
        return SparkMcpResult.Json(new
        {
            repository = Reference(resolved),
            branch = overview.Branch,
            latest = overview.Latest,
            totals = overview.Totals,
            trend = overview.Trend,
            largestDrops = overview.LargestDrops,
            branches = overview.Branches,
            note = overview.Latest is null ? "No coverage has been uploaded for this branch yet." : null,
        });
    }

    [McpServerTool(Name = "hotspots", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Files of the latest commit with coverage on a branch, ranked by uncovered lines, most first (ties by path). "
        + "coveredPercent and uncoveredRanges (runs of uncovered lines) are columns for you to weigh; they do not change the order. "
        + "Use codecoverage_file for a file's line numbers.")]
    public async Task<CallToolResult> HotspotsAsync(
        [Description("The repository as forge/owner/name, e.g. github/octo-org/octo-repo.")] string repository,
        [Description("The branch; the repository's default branch when omitted.")] string? branch = null,
        [Description("Only files under this path, e.g. src/services/.")] string? pathPrefix = null,
        [Description("Files to skip, for paging (use nextSkip from the previous page).")] int skip = 0,
        [Description("Files to return, 1 to 50.")] int take = 10,
        CancellationToken cancellationToken = default)
    {
        if (Paging(skip, take, max: 50) is { } invalid)
            return invalid;
        if (!await AllowedAsync(cancellationToken, ("Read", nameof(Repository)), ("Read", nameof(Commit))))
            return SparkMcpResult.Refuse();
        if (!TryParse(repository, out var forge, out var owner, out var name))
            return SparkMcpResult.Invalid(RepositoryFormat);
        if (await scope.ResolveAsync(forge, owner, name, cancellationToken) is not { } resolved)
            return SparkMcpResult.Refuse();

        var commit = await insights.PickCommitAsync(resolved, branch, sha: null, cancellationToken);
        if (commit is null)
            return SparkMcpResult.Json(new { repository = Reference(resolved), branch, note = "No coverage has been uploaded for this branch yet." });

        var page = await insights.HotspotsAsync(commit, pathPrefix, skip, take, cancellationToken);
        SparkMcpResult.Track(page.Files.Count);
        return SparkMcpResult.Json(new
        {
            repository = Reference(resolved),
            commit = commit.Sha,
            branch = commit.Branch,
            order = "uncoveredLines, most first; ties by path",
            totalFiles = page.TotalFiles,
            nextSkip = skip + page.Files.Count < page.TotalFiles ? skip + page.Files.Count : (int?)null,
            files = page.Files,
        });
    }

    [McpServerTool(Name = "file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One file's coverage at a commit (default: the latest with coverage on the default branch): its uncovered line ranges and the lines "
        + "whose branches were only partly taken. Line numbers only, no source: read those lines in your local checkout of that commit.")]
    public async Task<CallToolResult> FileAsync(
        [Description("The repository as forge/owner/name, e.g. github/octo-org/octo-repo.")] string repository,
        [Description("The file's path from the repository root, e.g. src/app/parser.ts.")] string path,
        [Description("The commit as its full 40-character sha; the latest with coverage on the default branch when omitted.")] string? commit = null,
        CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(cancellationToken, ("Read", nameof(Repository)), ("Read", nameof(Commit))))
            return SparkMcpResult.Refuse();
        if (!TryParse(repository, out var forge, out var owner, out var name))
            return SparkMcpResult.Invalid(RepositoryFormat);
        if (string.IsNullOrWhiteSpace(path))
            return SparkMcpResult.Invalid("Name the file by its path from the repository root.");
        if (commit is not null && !IsSha(commit))
            return SparkMcpResult.Invalid("Name the commit by its full 40-character sha, or leave it out for the latest.");
        if (await scope.ResolveAsync(forge, owner, name, cancellationToken) is not { } resolved)
            return SparkMcpResult.Refuse();

        var picked = await insights.PickCommitAsync(resolved, branch: null, commit, cancellationToken);
        if (picked is null)
            return SparkMcpResult.Json(new { repository = Reference(resolved), commit, note = "No coverage was uploaded for that commit." });

        var report = await insights.FileAsync(picked, path, cancellationToken);
        if (report is null)
            return SparkMcpResult.Json(new { repository = Reference(resolved), commit = picked.Sha, path, note = "This commit's coverage has no file at that path." });

        SparkMcpResult.Track(1);
        return SparkMcpResult.Json(new { repository = Reference(resolved), commit = picked.Sha, file = report });
    }

    [McpServerTool(Name = "patch", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Patch coverage of a pull request, from its newest commit with coverage: how many added lines tests ran, and per file the added lines "
        + "they did not run.")]
    public async Task<CallToolResult> PatchAsync(
        [Description("The repository as forge/owner/name, e.g. github/octo-org/octo-repo.")] string repository,
        [Description("The pull request number.")] int pullRequest,
        CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(cancellationToken, ("Read", nameof(Repository)), ("Read", nameof(Commit))))
            return SparkMcpResult.Refuse();
        if (!TryParse(repository, out var forge, out var owner, out var name))
            return SparkMcpResult.Invalid(RepositoryFormat);
        if (pullRequest < 1)
            return SparkMcpResult.Invalid("pullRequest must be a pull request number, 1 or more.");
        if (await scope.ResolveAsync(forge, owner, name, cancellationToken) is not { } resolved)
            return SparkMcpResult.Refuse();

        var report = await insights.PatchAsync(resolved, pullRequest, cancellationToken);
        if (report is null)
            return SparkMcpResult.Json(new { repository = Reference(resolved), pullRequest, note = "No coverage was uploaded for this pull request." });

        SparkMcpResult.Track(report.Files?.Count ?? 0);
        return SparkMcpResult.Json(new
        {
            repository = Reference(resolved),
            pullRequest,
            commit = report.HeadSha,
            branch = report.Branch,
            linesCovered = report.Patch?.LinesCovered,
            linesCoverable = report.Patch?.LinesCoverable,
            patchPercent = report.Patch is { LinesCoverable: > 0 } p ? CoverageInsights.Percent(p.LinesCovered, p.LinesCoverable) : (double?)null,
            filesInDiff = report.Patch?.FilesInDiff,
            filesMeasured = report.Patch?.FilesMatched,
            diffTruncated = report.Patch?.DiffTruncated,
            uncoveredFiles = report.Files,
            omittedFiles = report.OmittedFiles,
            note = report.Patch is null ? "No patch coverage was computed for this commit (no base to compare with)."
                : report.Files is null ? "The changed lines could not be read from the forge just now; the totals stand." : null,
        });
    }

    [McpServerTool(Name = "drops", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Commits that lowered line coverage against their parent commit, newest first, each with the files whose uncovered lines grew most.")]
    public async Task<CallToolResult> DropsAsync(
        [Description("The repository as forge/owner/name, e.g. github/octo-org/octo-repo.")] string repository,
        [Description("Only commits on this branch; every branch when omitted.")] string? branch = null,
        [Description("Only commits from this date on, e.g. 2026-09-01.")] string? since = null,
        [Description("Commits to return, 1 to 10.")] int take = 5,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 10)
            return SparkMcpResult.Invalid("take must be between 1 and 10.");
        if (!await AllowedAsync(cancellationToken, ("Read", nameof(Repository)), ("Query", nameof(Commit))))
            return SparkMcpResult.Refuse();
        if (!TryParse(repository, out var forge, out var owner, out var name))
            return SparkMcpResult.Invalid(RepositoryFormat);

        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return SparkMcpResult.Invalid("since must be a date, e.g. 2026-09-01.");
            from = parsed;
        }

        if (await scope.ResolveAsync(forge, owner, name, cancellationToken) is not { } resolved)
            return SparkMcpResult.Refuse();

        var drops = await insights.DropsAsync(resolved, branch, from, take, cancellationToken);
        SparkMcpResult.Track(drops.Count);
        return SparkMcpResult.Json(new { repository = Reference(resolved), drops });
    }

    /// <summary>Every right the call reads, through the permission service that applies the agent layers (D6, D13).</summary>
    private async Task<bool> AllowedAsync(CancellationToken cancellationToken, params (string Action, string Target)[] rights)
    {
        foreach (var (action, target) in rights)
        {
            if (!await permissions.IsAllowedAsync(action, target, cancellationToken))
                return false;
        }
        return true;
    }

    /// <summary>A repository as the tools name it: <c>github/owner/name</c>, the website's own URL segments.</summary>
    internal static string Reference(Repository repository) => $"{repository.Provider.ToCanonicalString()}/{repository.FullName}";

    /// <summary>
    /// <c>forge/owner/name</c>, three non-empty segments. The forge is passed on as written: an unknown one resolves to
    /// nothing in <see cref="IRepositoryScope"/>, and so answers like a missing repository.
    /// </summary>
    internal static bool TryParse(string? reference, out string forge, out string owner, out string name)
    {
        forge = owner = name = string.Empty;
        var parts = reference?.Trim().Split('/');
        if (parts is not { Length: 3 } || parts.Any(p => p.Length == 0 || p.Any(char.IsWhiteSpace)))
            return false;

        (forge, owner, name) = (parts[0], parts[1], parts[2]);
        return true;
    }

    private static bool IsSha(string value) => value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static CallToolResult? Paging(int skip, int take, int max)
        => skip < 0 || take < 1 || take > max
            ? SparkMcpResult.Invalid($"skip must be 0 or more and take between 1 and {max}.")
            : null;
}
