using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.Spark.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

/// <summary>
/// Read API for the browse UI: account → repositories → commits → build →
/// folder tree. Public repositories are world-readable; private ones require
/// the viewer's GitHub-verified access to the owner (no app-local ACL).
/// Anonymous requests simply see the public subset.
/// </summary>
[ApiController]
[Route("api/browse")]
// Anonymous for public repositories, and GetFile spends the installation's
// shared GitHub rate limit per uncached path — so a crawler here is a denial of
// service against every other tenant, not just a load on us. Partitioned by IP:
// these callers present no credential to key on.
[EnableRateLimiting("browse")]
// Previously carried no authorization attribute at all — reachable because
// nothing said otherwise, which is indistinguishable from an oversight. Browse
// is now a declared right held by both well-known roles, so the vanity pages
// keep working for anonymous visitors and the posture report says so out loud.
// Row-level scoping is unchanged and still lives in the method bodies: this
// right answers "may you use this API", not "may you see this repository".
[SparkAuthorize("Browse", "Coverage")]
public partial class BrowseController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IGitHubContentService gitHubContent;
    [Inject] private readonly IConfiguration configuration;

    public sealed record RepoInfo(string Id, string Owner, string Name, string FullName, bool IsPrivate, string? DefaultBranch,
        CoverageSummary? LatestCoverage, string? LatestCoverageSha, bool CanManage, string? BadgeToken, string? BaseUrl);
    public sealed record CommitInfo(string Sha, string? Branch, int? PullRequestNumber, string? Message,
        DateTimeOffset? AuthoredAt, CoverageSummary? Coverage);
    public sealed record BuildInfo(long RunId, int RunAttempt, string Status, string? FinalizeReason,
        string? WorkflowName, DateTime CreatedAtUtc, CoverageSummary? Coverage, IEnumerable<SessionInfo> Sessions);
    public sealed record SessionInfo(string SessionId, string? JobName, string[] Flags, string ParseStatus, string? Error, int FilesCount);
    public sealed record TreeEntry(string Name, string Path, bool IsFile, int LinesCovered, int LinesCoverable);
    public sealed record TreeResponse(string BuildId, IEnumerable<TreeEntry> Entries, IEnumerable<string> UnmatchedFiles,
        int UnmatchedTotal = 0);

    // UnmatchedFiles is a sample; UnmatchedTotal carries the real count so the
    // UI can disclose truncation ("showing 50 of 314") instead of passing the
    // cap off as the total (#13 U3).
    private const int UnmatchedSampleSize = 50;

    [HttpGet("accounts/{login}/repos")]
    public async Task<ActionResult<IEnumerable<RepoInfo>>> GetAccountRepos(string login, CancellationToken cancellationToken)
    {
        var includePrivate = await gitHubAccess.IsOwnerAllowedAsync(login, cancellationToken);

        var repos = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerLogin == login)
            .Take(1024)
            .ToListAsync(cancellationToken);

        return Ok(repos
            .Where(r => includePrivate || !r.IsPrivate)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => ToRepoInfo(r, includePrivate)));
    }

    [HttpGet("repos/{owner}/{name}")]
    public async Task<ActionResult<RepoInfo>> GetRepo(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();
        var canManage = await gitHubAccess.IsOwnerAllowedAsync(repository.OwnerLogin, cancellationToken);
        return Ok(ToRepoInfo(repository, canManage));
    }

    [HttpGet("repos/{owner}/{name}/commits")]
    public async Task<ActionResult<IEnumerable<CommitInfo>>> GetCommits(
        string owner, string name, [FromQuery] string? branch, [FromQuery] bool withCoverageOnly = true,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var query = session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id);
        if (!string.IsNullOrEmpty(branch))
            query = query.Where(c => c.Branch == branch);
        if (withCoverageOnly)
            query = query.Where(c => c.HasCoverage);

        var commits = await query
            .OrderByDescending(c => c.AuthoredAt)
            .Skip(skip)
            .Take(Math.Min(take, 200))
            .OfType<Commit>()
            .ToListAsync(cancellationToken);

        return Ok(commits.Select(c => new CommitInfo(c.Sha, c.Branch, c.PullRequestNumber, c.Message, c.AuthoredAt, c.Coverage)));
    }

    public sealed record HistoryPoint(string Sha, DateTimeOffset? Timestamp, int LinesCovered, int LinesCoverable, double Percent);

    /// <summary>
    /// Coverage over time for one repository (ascending, ready for
    /// bs-trend-chart). Timestamp is AuthoredAt coalesced with FirstSeenAtUtc
    /// and may still be null for documents that predate either stamp.
    /// <para>
    /// Scoped to <paramref name="branch"/>, defaulting to the repository's default
    /// branch — a trend that mixes branches is not a trend. Repositories with no
    /// known default branch fall back to every branch; see the body.
    /// </para>
    /// </summary>
    [HttpGet("repos/{owner}/{name}/history")]
    public async Task<ActionResult<IEnumerable<HistoryPoint>>> GetHistory(
        string owner, string name, [FromQuery] string? branch, [FromQuery] int take = 100, CancellationToken cancellationToken = default)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        // Commits are ordered by time alone, so without a branch filter a feature
        // branch's points interleave with the default branch's and the line
        // zig-zags between two unrelated populations. The badge beside this chart
        // has always been default-branch-scoped (BadgeController.LoadBranchCoverage);
        // this makes the chart agree with it.
        //
        // DefaultBranch is null for repositories provisioned by an OIDC upload — it
        // is only ever written from a GitHub webhook payload — and filtering on null
        // would render an empty chart. Falling back to every branch is what
        // BuildFinalizer and BaseResolver do in the same situation, and for a
        // repository with effectively one branch it is the same picture anyway.
        var effectiveBranch = string.IsNullOrEmpty(branch) ? repository.DefaultBranch : branch;

        var query = session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id && c.HasCoverage);
        if (!string.IsNullOrEmpty(effectiveBranch))
            query = query.Where(c => c.Branch == effectiveBranch);

        var commits = await query
            .OrderByDescending(c => c.AuthoredAt)
            .Take(Math.Clamp(take, 1, 500))
            .OfType<Commit>()
            .ToListAsync(cancellationToken);

        commits.Reverse();
        return Ok(commits
            .Where(c => c.Coverage is { LinesCoverable: > 0 })
            .Select(c => new HistoryPoint(c.Sha, c.AuthoredAt ?? c.FirstSeenAtUtc,
                c.Coverage!.LinesCovered, c.CodeCoverage.LinesCoverable,
                Math.Round(c.CodeCoverage.LinesCovered * 100.0 / c.CodeCoverage.LinesCoverable, 1))));
    }

    /// <summary>
    /// Recent coverage percentages per repository of an account (ascending),
    /// for table sparklines. One query: the newest ~1000 covered commits
    /// across the account, grouped per repo — sparsely-uploaded repos may
    /// show fewer points, which is fine for a sparkline.
    /// <para>
    /// Each repository's points are scoped to its own default branch, for the same
    /// reason as <see cref="GetHistory"/>. The scoping is applied after the fetch
    /// because the branch differs per repository, so a repository with many
    /// feature-branch uploads may contribute fewer points than one without —
    /// acceptable for a sparkline, and better than a line that mixes branches.
    /// </para>
    /// </summary>
    [HttpGet("accounts/{login}/sparklines")]
    public async Task<ActionResult<Dictionary<string, double[]>>> GetSparklines(string login, CancellationToken cancellationToken)
    {
        var includePrivate = await gitHubAccess.IsOwnerAllowedAsync(login, cancellationToken);

        var repos = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerLogin == login)
            .Take(1024)
            .ToListAsync(cancellationToken);
        var visible = repos.Where(r => includePrivate || !r.IsPrivate).ToDictionary(r => r.Id!, r => r);
        if (visible.Count == 0) return Ok(new Dictionary<string, double[]>());

        var repoIds = visible.Keys.ToArray();
        var commits = await session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository.In(repoIds) && c.HasCoverage)
            .OrderByDescending(c => c.AuthoredAt)
            .Take(1000)
            .OfType<Commit>()
            .ToListAsync(cancellationToken);

        var result = commits
            .Where(c => c.Repository is not null && c.Coverage is { LinesCoverable: > 0 })
            .Where(c => visible.TryGetValue(c.Repository!, out var repo)
                        && (repo.DefaultBranch is null
                            || string.Equals(c.Branch, repo.DefaultBranch, StringComparison.Ordinal)))
            .GroupBy(c => c.Repository!)
            .ToDictionary(
                g => visible[g.Key].FullName,
                g => g.Take(20)
                      .Reverse()
                      .Select(c => Math.Round(c.Coverage!.LinesCovered * 100.0 / c.CodeCoverage.LinesCoverable, 1))
                      .ToArray());

        return Ok(result);
    }

    /// <summary>Distinct branches that have commits with coverage, default branch first.</summary>
    [HttpGet("repos/{owner}/{name}/branches")]
    public async Task<ActionResult<IEnumerable<string>>> GetBranches(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var branches = await session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id && c.HasCoverage)
            .Select(c => c.Branch)
            .Distinct()
            .Take(200)
            .ToListAsync(cancellationToken);

        return Ok(branches
            .Where(b => !string.IsNullOrEmpty(b))
            .OrderByDescending(b => b == repository.DefaultBranch)
            .ThenBy(b => b, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Public account reference (logins/avatars are public GitHub data). The
    /// document id feeds the generic Spark sub-queries as parentId.
    /// </summary>
    [HttpGet("accounts/{login}")]
    public async Task<ActionResult<AccountRef>> GetAccount(string login, CancellationToken cancellationToken)
    {
        var account = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.Login == login)
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null) return NotFound();
        return Ok(new AccountRef(account.Id!, account.Login));
    }

    public sealed record AccountRef(string Id, string Login);

    [HttpGet("repos/{owner}/{name}/commits/{sha}")]
    public async Task<ActionResult<object>> GetCommit(string owner, string name, string sha, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.GitHubId, sha), cancellationToken);
        if (commit is null) return NotFound();

        var builds = new List<Build>();
        var buildsPrefix = $"{Commit.DocumentId(repository.GitHubId, sha)}/builds/";
        await using (var stream = await session.Advanced.StreamAsync<Build>(
            startsWith: buildsPrefix, token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                // Only DIRECT children of /builds/ are Build documents. Deeper
                // ids share the prefix — FileCoverage ({buildId}/files/{hash})
                // and BuildTreeSummary ({buildId}/tree) — and would deserialize
                // into phantom all-default builds (run "0.0", year-1 dates),
                // one per covered file.
                if (stream.Current.Id is { } id && !id.AsSpan(buildsPrefix.Length).Contains('/'))
                    builds.Add(stream.Current.Document);
            }
        }

        return Ok(new
        {
            // Document id — the generic Spark sub-queries on the commit page
            // need it as parentId.
            commit.Id,
            commit.Sha,
            commit.Branch,
            commit.PullRequestNumber,
            commit.Message,
            commit.AuthoredAt,
            commit.Coverage,
            commit.LatestBuildId,
            // Per-flag totals of the build the page shows; null before flags
            // had storage. Keys are sanitized flag names — the same values
            // GetTree's ?flag= accepts.
            FlagTotals = builds.FirstOrDefault(b => b.Id == commit.LatestBuildId)?.FlagCoverage,
            Builds = builds
                .OrderByDescending(b => b.CreatedAtUtc)
                .Select(b => new BuildInfo(b.CiRunId, b.CiRunAttempt, b.Status, b.FinalizeReason, b.WorkflowName,
                    b.CreatedAtUtc, b.Coverage,
                    b.Sessions.Select(s => new SessionInfo(s.SessionId, s.JobName, s.Flags, s.ParseStatus, s.Error, s.FilesCount)))),
        });
    }

    /// <summary>One folder level of the commit's coverage tree (drill-down UI).</summary>
    [HttpGet("repos/{owner}/{name}/commits/{sha}/tree")]
    public async Task<ActionResult<TreeResponse>> GetTree(
        string owner, string name, string sha, [FromQuery] string? path, [FromQuery] string? flag, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.GitHubId, sha), cancellationToken);
        if (commit?.LatestBuildId is null) return NotFound();

        // A flag narrows the tree to that flag's own merged documents; an
        // unknown flag is an empty tree, not an error — flags are user labels.
        List<TreeFileSummary> files;
        if (!string.IsNullOrEmpty(flag))
        {
            var flagTree = await session.LoadAsync<BuildTreeSummary>(
                BuildTreeSummary.FlagDocumentId(commit.LatestBuildId, flag), cancellationToken);
            files = flagTree?.Files ?? [];
        }
        else
        {
            files = await LoadTreeSummaries(commit.LatestBuildId, cancellationToken);
        }

        var prefix = string.IsNullOrEmpty(path) ? "" : path.TrimEnd('/') + "/";
        var folders = new Dictionary<string, (int Covered, int Coverable)>(StringComparer.Ordinal);
        var entries = new List<TreeEntry>();

        foreach (var file in files.Where(f => f.Matched && f.Path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = file.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                entries.Add(new TreeEntry(rest, file.Path, true, file.LinesCovered, file.LinesCoverable));
            }
            else
            {
                var folder = rest[..slash];
                var current = folders.GetValueOrDefault(folder);
                folders[folder] = (current.Covered + file.LinesCovered, current.Coverable + file.LinesCoverable);
            }
        }

        var result = folders
            .Select(kv => new TreeEntry(kv.Key, prefix + kv.Key, false, kv.Value.Covered, kv.Value.Coverable))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

        List<string> unmatched = string.IsNullOrEmpty(path)
            ? files.Where(f => !f.Matched).Select(f => f.Path).Take(UnmatchedSampleSize).ToList()
            : [];
        var unmatchedTotal = string.IsNullOrEmpty(path) ? files.Count(f => !f.Matched) : 0;

        return Ok(new TreeResponse(commit.LatestBuildId, result, unmatched, unmatchedTotal));
    }

    /// <summary>
    /// The commit's full coverage tree in bs-hierarchy-chart's HierarchyNode
    /// shape: leaves carry value (coverable lines) + colorValue (covered %);
    /// folders carry neither — the chart derives folder colors as
    /// value-weighted means and sizes arcs by summed leaf values.
    /// </summary>
    [HttpGet("repos/{owner}/{name}/commits/{sha}/hierarchy")]
    public async Task<ActionResult<HierarchyNodeDto>> GetHierarchy(string owner, string name, string sha, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.GitHubId, sha), cancellationToken);
        if (commit?.LatestBuildId is null) return NotFound();

        var files = await LoadTreeSummaries(commit.LatestBuildId, cancellationToken);

        var root = new HierarchyNodeDto { Id = "/", Name = repository.Name, Children = [] };
        foreach (var file in files.Where(f => f.Matched).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var segments = file.Path.Split('/');
            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var folderPath = string.Join('/', segments[..(i + 1)]);
                var next = current.Children!.FirstOrDefault(c => c.Id == folderPath);
                if (next is null)
                {
                    next = new HierarchyNodeDto { Id = folderPath, Name = segments[i], Children = [] };
                    current.Children!.Add(next);
                }
                current = next;
            }

            current.Children!.Add(new HierarchyNodeDto
            {
                Id = file.Path,
                Name = segments[^1],
                Value = file.LinesCoverable,
                ColorValue = file.LinesCoverable == 0 ? null
                    : Math.Round(file.LinesCovered * 100.0 / file.LinesCoverable, 1),
            });
        }

        return Ok(root);
    }

    public sealed class HierarchyNodeDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? Value { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? ColorValue { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<HierarchyNodeDto>? Children { get; set; }
    }

    /// <summary>
    /// One file's line coverage plus its source at that commit (fetched from
    /// GitHub live — never stored). Source may be null (uninstalled private
    /// repo, deleted history, binary); the UI then shows coverage-only rows.
    /// </summary>
    [HttpGet("repos/{owner}/{name}/commits/{sha}/file")]
    public async Task<ActionResult<object>> GetFile(
        string owner, string name, string sha, [FromQuery] string path, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.GitHubId, sha), cancellationToken);
        if (commit?.LatestBuildId is null) return NotFound();

        var fileCoverage = await session.LoadAsync<FileCoverage>(
            FileCoverage.DocumentId(commit.LatestBuildId, path), cancellationToken);
        if (fileCoverage is null) return NotFound();

        long? installationId = null;
        if (repository.Account is not null)
        {
            var account = await session.LoadAsync<Account>(repository.Account, cancellationToken);
            installationId = account?.InstallationId;
        }

        var source = await gitHubContent.GetFileContentAsync(repository, installationId, sha, path, cancellationToken);

        return Ok(new
        {
            fileCoverage.Path,
            Source = source,
            Lines = fileCoverage.Lines,
            Branches = fileCoverage.Branches,
        });
    }

    /// <summary>
    /// The build's per-file totals: one point-load of the summary materialized
    /// at finalize. Builds finalized before the summary existed fall back to
    /// streaming their FileCoverage documents and deriving it on the fly.
    /// </summary>
    private async Task<List<TreeFileSummary>> LoadTreeSummaries(string buildId, CancellationToken cancellationToken)
    {
        var summary = await session.LoadAsync<BuildTreeSummary>(BuildTreeSummary.DocumentId(buildId), cancellationToken);
        if (summary is not null)
            return summary.Files;

        var files = new List<TreeFileSummary>();
        await using var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{buildId}/files/", token: cancellationToken);
        while (await stream.MoveNextAsync())
        {
            var file = stream.Current.Document;
            files.Add(new TreeFileSummary
            {
                Path = file.Path,
                Matched = file.Matched,
                LinesCovered = file.Lines.Count(l => l.Status != LineStatus.NotCovered),
                LinesCoverable = file.Lines.Count,
            });
        }
        return files;
    }

    private async Task<Repository?> ResolveVisibleRepository(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null) return null;

        // Same rule as the /spark surface, from the same place — the two must
        // agree forever, and a shared doc-comment was the only thing binding
        // them. The owner list is only fetched when it can matter.
        if (!repository.IsPrivate) return repository;
        var owners = await gitHubAccess.GetAllowedOwnersAsync(cancellationToken);
        return RepositoryVisibility.IsVisible(repository, owners) ? repository : null;
    }

    // BaseUrl rides along so the SPA builds badge markdown against the public
    // URL rather than location.origin (dead links when copied from localhost).
    private RepoInfo ToRepoInfo(Repository r, bool canManage)
        // Id first: the /r/{owner}/{name} route resolves it to forward into the
        // generic Spark detail page.
        => new(r.Id!, r.OwnerLogin, r.Name, r.FullName, r.IsPrivate, r.DefaultBranch, r.LatestCoverage, r.LatestCoverageSha,
            canManage, canManage ? r.BadgeToken : null, configuration["Coverage:BaseUrl"]?.TrimEnd('/'));
}
