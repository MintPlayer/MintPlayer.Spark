using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Builds a commit's <see cref="CommitAssembly"/>: the max-merge of every
/// finalized build's files (measured), plus files copied from the base commit
/// when this commit's builds were partial and the file's git blob OID is the
/// same at both ends (carried). Measured always beats carried. Then stamps the
/// commit headline, the two Δ columns, and the repository promotion.
/// </summary>
[Register(typeof(ICommitAssembler), ServiceLifetime.Scoped)]
public partial class CommitAssembler : ICommitAssembler
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IBaseResolver baseResolver;
    [Inject] private readonly IGitHubDiffService diffService;
    [Inject] private readonly ILogger<CommitAssembler> logger;

    private const int LoadChunk = 512;
    private const int DependantLimit = 200;

    public async Task<CommitAssembly?> AssembleAsync(string commitId, CancellationToken cancellationToken = default)
    {
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var commit = await session.LoadAsync<Commit>(commitId, cancellationToken);
        if (commit is null)
            return null;

        var repository = commit.Repository is null ? null : await session.LoadAsync<Repository>(commit.Repository, cancellationToken);

        var builds = await LoadContributingBuilds(commitId, cancellationToken);
        if (builds.Count == 0)
            return null;

        // 1. Measured: union of every contributing build, max-merged per path.
        var files = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
        foreach (var build in builds)
        {
            await using var stream = await session.Advanced.StreamAsync<FileCoverage>(startsWith: $"{build.Id}/files/", token: cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var source = stream.Current.Document;
                if (files.TryGetValue(source.Path, out var target))
                {
                    CoverageMerger.MergeInto(target, source);
                }
                else
                {
                    var copy = CoverageMerger.Clone(source);
                    copy.Origin = new FileOrigin { Kind = FileOrigin.Measured, FromSha = commit.Sha, FromBuildId = build.Id, OriginSha = commit.Sha };
                    files[source.Path] = copy;
                }
            }
        }
        var measuredCount = files.Count;

        // 2. The head tree as the uploaders saw it (any session's file list; OIDs unioned).
        var head = await LoadHeadFileList(builds, cancellationToken);

        var assembly = new CommitAssembly
        {
            Commit = commitId,
            Repository = commit.Repository,
            Sha = commit.Sha,
            Builds = [.. builds.Select(b => new AssemblyBuild
            {
                BuildId = b.Id!, CiRunId = b.CiRunId, CiRunAttempt = b.CiRunAttempt,
                Partial = b.Partial, CarryForward = b.CarryForward, DeclaredBaseSha = b.DeclaredBaseSha,
            })],
            HeadFileCount = head.Paths.Count,
            HeadHasOids = head.HasOids,
            MeasuredFiles = measuredCount,
            AssembledAtUtc = DateTime.UtcNow,
        };

        // 3. Carry-forward, only when something declared itself partial.
        var anyPartial = builds.Any(b => b.Partial);
        if (anyPartial)
            await CarryForward(commit, repository, builds, head, files, assembly, cancellationToken);

        // 4. Completeness: a full upload is complete; a partial one only when
        //    every changed file the base knew about was re-measured and the base
        //    itself was trustworthy.
        assembly.Completeness = !anyPartial || (assembly.IncompleteReasons.Count == 0 && assembly.UnmeasuredFiles == 0)
            ? CommitAssembly.Complete
            : CommitAssembly.Partial;
        if (assembly.UnmeasuredFiles > 0 && !assembly.IncompleteReasons.Contains(CommitAssembly.ReasonUnmeasuredChanges))
            assembly.IncompleteReasons.Add(CommitAssembly.ReasonUnmeasuredChanges);

        // 5. Materialize.
        await WriteAssembledFiles(commitId, files.Values, cancellationToken);
        assembly.Coverage = CoverageMerger.Summarize(files.Values);
        assembly.OldestOriginSha = await OldestOrigin(repository, files.Values, cancellationToken);
        await session.StoreAsync(assembly, CommitAssembly.DocumentId(commitId), cancellationToken);

        commit.Coverage = assembly.Coverage;
        commit.AssemblyCompleteness = assembly.Completeness;
        commit.LatestBuildId = builds[^1].Id;

        Promote(commit, repository, assembly);
        await StampDeltas(commit, repository, cancellationToken);

        return assembly;
    }

    public async Task RestampDeltasAsync(string commitId, CancellationToken cancellationToken = default)
    {
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var commit = await session.LoadAsync<Commit>(commitId, cancellationToken);
        if (commit is null)
            return;
        var repository = commit.Repository is null ? null : await session.LoadAsync<Repository>(commit.Repository, cancellationToken);
        await StampDeltas(commit, repository, cancellationToken);
    }

    /// <summary>Finalized builds of the commit, highest attempt per run, oldest finalize first.</summary>
    private async Task<List<Build>> LoadContributingBuilds(string commitId, CancellationToken cancellationToken)
    {
        var prefix = $"{commitId}/builds/";
        var loaded = await session.Advanced.LoadStartingWithAsync<Build>(prefix, exclude: "*/files/*|*/flags/*|*/tree", pageSize: 256, token: cancellationToken);
        return loaded
            .Where(b => b.Id is not null && !b.Id.AsSpan(prefix.Length).Contains('/'))
            .Where(b => b.Status == "Finalized")
            .GroupBy(b => b.CiRunId)
            .Select(g => g.OrderByDescending(b => b.CiRunAttempt).First())
            .OrderBy(b => b.FinalizedAtUtc ?? DateTime.MaxValue)
            .ThenBy(b => b.CiRunId)
            .ToList();
    }

    private async Task<HeadFileList> LoadHeadFileList(List<Build> builds, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        // Newest build first so its OIDs win when lists disagree (they shouldn't).
        foreach (var build in builds.AsEnumerable().Reverse())
        {
            foreach (var buildSession in build.Sessions)
            {
                var text = await BuildAttachments.ReadTextAsync(session, build, UploadAttachments.FileListName(buildSession.SessionId), cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }
        }
        // HeadFileList keeps the first OID per path, so concatenation order = precedence.
        return HeadFileList.Parse(string.Join('\n', parts));
    }

    private async Task CarryForward(Commit commit, Repository? repository, List<Build> builds, HeadFileList head,
        Dictionary<string, FileCoverage> files, CommitAssembly assembly, CancellationToken cancellationToken)
    {
        var reasons = assembly.IncompleteReasons;

        if (builds.Any(b => !b.CarryForward))
        {
            // A crashed suite emits no report; carrying the base's numbers over
            // the hole would paint the crash green.
            reasons.Add(CommitAssembly.ReasonTestsFailed);
            return;
        }

        var declared = builds.Select(b => b.DeclaredBaseSha).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        assembly.BaseRequestedSha = declared.FirstOrDefault();
        if (declared.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            reasons.Add(CommitAssembly.ReasonBaseMismatch);

        if (repository is null)
        {
            reasons.Add(CommitAssembly.ReasonNoBase);
            return;
        }

        var resolved = await baseResolver.ResolveAsync(repository, commit, assembly.BaseRequestedSha, cancellationToken);
        assembly.BaseResolution = resolved.Mode;
        if (resolved.ResolvedSha is null)
        {
            reasons.Add(CommitAssembly.ReasonNoBase);
            return;
        }
        assembly.BaseSha = resolved.ResolvedSha;
        if (resolved.Mode == ResolvedBase.Walked)
            reasons.Add(CommitAssembly.ReasonBaseWalked);

        if (head.Count == 0)
        {
            reasons.Add(CommitAssembly.ReasonNoFileList);
            return;
        }
        var baseCommitId = Entities.Commit.DocumentId(repository.GitHubId, resolved.ResolvedSha);
        var baseHasAssembly = await session.Advanced.ExistsAsync(CommitAssembly.DocumentId(baseCommitId), cancellationToken);
        if (!baseHasAssembly && resolved.BaseBuildId is null)
        {
            reasons.Add(CommitAssembly.ReasonNoBase);
            return;
        }

        // C′: an old action build sent bare paths. GitHub's compare API can
        // still tell which files changed between base and head — unless it
        // truncated the list at 300 files, in which case the changed set is
        // unknown and nothing is carried.
        HashSet<string>? changedSinceBase = null;
        if (!head.HasOids)
        {
            changedSinceBase = await ChangedFilesViaCompare(repository, resolved.ResolvedSha, commit.Sha, cancellationToken);
            if (changedSinceBase is null)
            {
                reasons.Add(CommitAssembly.ReasonNoBlobIds);
                return;
            }
        }

        var candidates = head.Paths
            .Where(p => !files.ContainsKey(p))
            .Select(p => (Path: p, Oid: head.OidFor(p)))
            .Where(c => c.Oid is not null || changedSinceBase is not null)
            .ToList();

        var carried = 0;
        var unmeasured = 0;
        foreach (var chunk in candidates.Chunk(LoadChunk))
        {
            var ids = chunk.Select(c => baseHasAssembly
                ? CommitAssembly.FileDocumentId(baseCommitId, c.Path)
                : FileCoverage.DocumentId(resolved.BaseBuildId!, c.Path)).ToArray();
            var loaded = await session.LoadAsync<FileCoverage>(ids, cancellationToken);

            for (var i = 0; i < chunk.Length; i++)
            {
                if (!loaded.TryGetValue(ids[i], out var baseFile) || baseFile is null)
                    continue; // the base never knew this file: nothing to carry, nothing missing

                var unchanged = changedSinceBase is not null
                    ? !changedSinceBase.Contains(chunk[i].Path)
                    : baseFile.BlobOid is not null && string.Equals(baseFile.BlobOid, chunk[i].Oid, StringComparison.OrdinalIgnoreCase);
                if (!unchanged)
                {
                    unmeasured++; // changed since the base and not re-measured here
                    continue;
                }

                var copy = CoverageMerger.Clone(baseFile);
                copy.Matched = true;
                copy.BlobOid = chunk[i].Oid ?? baseFile.BlobOid;
                copy.Origin = new FileOrigin
                {
                    Kind = FileOrigin.Carried,
                    FromSha = resolved.ResolvedSha,
                    FromBuildId = baseFile.Origin?.FromBuildId ?? baseFile.BuildId,
                    OriginSha = baseFile.Origin?.OriginSha ?? resolved.ResolvedSha,
                };
                files[chunk[i].Path] = copy;
                carried++;
            }
            session.Advanced.Evict(loaded.Values.Where(v => v is not null).ToArray());
        }

        assembly.CarriedFiles = carried;
        assembly.UnmeasuredFiles = unmeasured;
    }

    /// <summary>
    /// The set of repo paths GitHub reports as changed between base and head
    /// (new and previous names of renames both count), or null when the API
    /// is unreachable or truncated the list — "unknown", never "nothing".
    /// </summary>
    private async Task<HashSet<string>?> ChangedFilesViaCompare(Repository repository, string baseSha, string headSha, CancellationToken cancellationToken)
    {
        long? installationId = repository.Account is null ? null
            : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;
        var comparison = await diffService.CompareAsync(repository, installationId, baseSha, headSha, cancellationToken);
        if (comparison is null || comparison.Truncated)
            return null;

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in comparison.Files)
        {
            changed.Add(HeadFileList.Unify(file.Path));
            if (file.PreviousPath is not null)
                changed.Add(HeadFileList.Unify(file.PreviousPath));
        }
        return changed;
    }

    /// <summary>Overwrites the assembled file documents and deletes the ones a previous assembly wrote that no longer exist.</summary>
    private async Task WriteAssembledFiles(string commitId, IEnumerable<FileCoverage> files, CancellationToken cancellationToken)
    {
        var prefix = CommitAssembly.FilesPrefix(commitId);
        var stale = new HashSet<string>(StringComparer.Ordinal);
        await using (var stream = await session.Advanced.StreamAsync<FileCoverage>(startsWith: prefix, token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
                stale.Add(stream.Current.Id);
        }

        var tree = new BuildTreeSummary { BuildId = CommitAssembly.DocumentId(commitId) };
        foreach (var file in files)
        {
            var id = CommitAssembly.FileDocumentId(commitId, file.Path);
            stale.Remove(id);
            file.Id = id;
            await session.StoreAsync(file, id, cancellationToken);
            tree.Files.Add(new TreeFileSummary
            {
                Path = file.Path,
                Matched = file.Matched,
                LinesCovered = file.Lines.Count(l => l.Status != LineStatus.NotCovered),
                LinesCoverable = file.Lines.Count,
                Origin = file.Origin?.Kind,
                CarriedFromSha = file.Origin?.Kind == FileOrigin.Carried ? file.Origin.FromSha : null,
            });
        }
        foreach (var id in stale)
            session.Delete(id);

        await session.StoreAsync(tree, CommitAssembly.TreeDocumentId(commitId), cancellationToken);
    }

    private async Task<string?> OldestOrigin(Repository? repository, IEnumerable<FileCoverage> files, CancellationToken cancellationToken)
    {
        var origins = files
            .Where(f => f.Origin?.Kind == FileOrigin.Carried && f.Origin.OriginSha is not null)
            .Select(f => f.Origin!.OriginSha!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (origins.Count == 0)
            return null;
        if (origins.Count == 1 || repository is null)
            return origins[0];

        var ids = origins.Select(sha => Entities.Commit.DocumentId(repository.GitHubId, sha)).ToArray();
        var commits = await session.LoadAsync<Commit>(ids, cancellationToken);
        return commits.Values
            .Where(c => c is not null)
            .OrderBy(c => c!.Date ?? DateTimeOffset.MaxValue)
            .Select(c => c!.Sha)
            .FirstOrDefault() ?? origins[0];
    }

    /// <summary>
    /// Repo-level coverage tracks the default branch; a repo that never had data
    /// accepts any branch rather than showing nothing. Never a partial assembly:
    /// its total is a subset's, and the badge serves this number.
    /// </summary>
    private static void Promote(Commit commit, Repository? repository, CommitAssembly assembly)
    {
        if (repository is null || assembly.Completeness != CommitAssembly.Complete)
            return;
        if (repository.LatestCoverage is not null
            && repository.DefaultBranch is not null
            && !string.Equals(commit.Branch, repository.DefaultBranch, StringComparison.Ordinal))
            return;

        repository.LatestCoverage = assembly.Coverage;
        repository.LatestCoverageSha = commit.Sha;
        repository.LatestCoverageAtUtc = DateTime.UtcNow;
    }

    private async Task StampDeltas(Commit commit, Repository? repository, CancellationToken cancellationToken)
    {
        if (repository is null)
            return;

        // The API is authoritative for the parent: older action builds sent the
        // PR base sha under this name, and a Δ against the wrong commit is worse
        // than none.
        long? installationId = repository.Account is null ? null
            : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;
        var apiParent = await diffService.GetFirstParentAsync(repository, installationId, commit.Sha, cancellationToken);
        commit.ParentLookupAttemptedAtUtc = DateTime.UtcNow;
        if (apiParent is not null)
        {
            commit.ParentSha = apiParent;
            commit.ParentShaSource = "api";
        }

        var percent = Percent(commit.Coverage);

        // Trust the parent when GitHub confirmed it, or when the action sent it
        // for a push: the old action only ever sent a (wrong, PR-base) parent on
        // pull_request events, so an upload-sourced parent on a PR commit may
        // still be that legacy value.
        var parentTrusted = commit.ParentShaSource == "api"
            || (commit.ParentShaSource == "upload" && commit.PullRequestNumber is null);

        commit.CoverageDeltaVsParent = null;
        if (commit.ParentSha is not null && parentTrusted && percent is not null)
        {
            var parent = await session.LoadAsync<Commit>(Entities.Commit.DocumentId(repository.GitHubId, commit.ParentSha), cancellationToken);
            commit.CoverageDeltaVsParent = Delta(percent, Percent(parent?.Coverage));
        }

        commit.CoverageDeltaVsDefaultBranch = percent is null ? null
            : Delta(percent, Percent((await NewestCompleteDefaultBranchCommit(repository, commit, cancellationToken))?.Coverage));

        await RestampDependants(commit, repository, percent, cancellationToken);
    }

    /// <summary>The default branch's newest complete commit dated at or before <paramref name="commit"/>, excluding itself.</summary>
    private async Task<Commit?> NewestCompleteDefaultBranchCommit(Repository repository, Commit commit, CancellationToken cancellationToken)
    {
        if (repository.DefaultBranch is null || commit.Date is null)
            return null;

        var date = commit.Date.Value;
        var candidates = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repository.Id && r.Branch == repository.DefaultBranch && r.CompleteCoverage && r.AuthoredAt <= date)
            .OrderByDescending(r => r.AuthoredAt)
            .OfType<Commit>()
            .Take(5)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(c => !string.Equals(c.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase) && c.Coverage is not null);
    }

    /// <summary>
    /// Children (ParentSha == this sha) get their vs-parent Δ from this new
    /// headline; if this is a complete default-branch commit, later commits that
    /// may now reference it get their vs-default-branch Δ recomputed.
    /// </summary>
    private async Task RestampDependants(Commit commit, Repository repository, double? percent, CancellationToken cancellationToken)
    {
        var children = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repository.Id && r.ParentSha == commit.Sha && r.HasCoverage)
            .OfType<Commit>()
            .Take(DependantLimit)
            .ToListAsync(cancellationToken);
        foreach (var child in children)
        {
            if (string.Equals(child.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
                continue;
            child.CoverageDeltaVsParent = Delta(Percent(child.Coverage), percent);
        }

        if (repository.DefaultBranch is null
            || !string.Equals(commit.Branch, repository.DefaultBranch, StringComparison.Ordinal)
            || commit.AssemblyCompleteness != CommitAssembly.Complete
            || commit.Date is null)
            return;

        var date = commit.Date.Value;
        var later = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repository.Id && r.HasCoverage && r.AuthoredAt > date)
            .OrderBy(r => r.AuthoredAt)
            .OfType<Commit>()
            .Take(DependantLimit)
            .ToListAsync(cancellationToken);
        foreach (var other in later)
        {
            if (string.Equals(other.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
                continue;
            var reference = await NewestCompleteDefaultBranchCommit(repository, other, cancellationToken);
            other.CoverageDeltaVsDefaultBranch = Delta(Percent(other.Coverage), Percent(reference?.Coverage));
        }
    }

    private static double? Percent(CoverageSummary? summary)
        => summary is { LinesCoverable: > 0 } ? summary.LinesCovered * 100d / summary.LinesCoverable : null;

    private static double? Delta(double? current, double? reference)
        => current is null || reference is null ? null : current - reference;
}
