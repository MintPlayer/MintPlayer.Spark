using CodeCoverage.Git;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/promote</c> (segment P).</summary>
/// <param name="Commit">The green default-branch commit every line moves to.</param>
/// <param name="Settings">For the change detection of a line with <see cref="PromoteLine.OnlyIfChanged"/> (P4).</param>
/// <param name="Versions">The versions to release (P5), each placed on the promotion commit of its package's line.</param>
/// <param name="Recorded">The tag names among <paramref name="Versions"/> that were recorded earlier (a hand tag) rather
/// than asked for now: one that no longer fits is reported and dropped instead of refusing its line.</param>
public sealed record PromoteJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string DefaultBranch, string Commit, GitIdentity Committer, IReadOnlyList<PromoteLine> Lines,
    VersioningSettings Settings, IReadOnlyList<VersionTag> Versions, IReadOnlySet<string> Recorded);

/// <summary>A validated <c>POST /jobs/versioning/hand-tag</c> (P5): a person's new tag, leased on <paramref name="TagObject"/>.</summary>
/// <param name="Lines">The repository's current lines; a version tag must fit the one of its package's ecosystem.</param>
public sealed record HandTagJob(string Repo, Uri CloneUrl, GitCredential Credential, VersionTag Tag, string TagObject, IReadOnlyList<PromoteLine> Lines,
    VersioningSettings Settings);

/// <summary>A line to promote: its branch, ecosystem and major, and whether it moves only when that ecosystem changed (P4).</summary>
public sealed record PromoteLine(string Branch, string Ecosystem, int Major, bool OnlyIfChanged);

/// <summary>
/// The promotion of release lines (segment P, <c>docs/release_lines_recovery_plan.md</c> §2b): each line gets one merge
/// commit whose tree is the default-branch commit's and whose parents are the line's tip and that commit, so the line's
/// tree equals a commit the default branch tested and the line's history keeps every commit it had (Q-A = B).
/// </summary>
/// <remarks>
/// <para>Nothing on a line is dropped silently (P1). The line's own changes since its merge base with the commit are
/// compared path by path: a path the line changed is fine when the commit holds the same content, or when some
/// default-branch commit in between wrote exactly the line's content (an older copy, such as a backport of a change the
/// default branch has since changed again). Any other path is line-only, and the line is refused with those paths and the
/// line commits that touched them.</para>
/// <para>Each line stands on its own: a refusal on one holds back no other. The push carries a lease on the tip that was
/// checked, so a line that moved during the job is reported, never overwritten.</para>
/// <para>A requested version (P5) is checked against the commit's graph: its id is a package, the package's ecosystem is
/// a line of the request, the major is that line's, and the version is above every tag of the package. It is pushed as a
/// lightweight tag on the promotion commit in the same atomic push as the line, so the line's release run never sees the
/// line without it; lightweight, so the engine reads it as a hand tag and releases exactly that version. A failing
/// version refuses its line, so a release never ships at a version nobody asked for; a line with a version moves even
/// when its ecosystem did not change.</para>
/// </remarks>
public sealed class PromoteService(JobRunner jobs)
{
    public const string Promoted = "promoted";
    /// <summary>The line holds the commit already.</summary>
    public const string Contained = "contained";
    /// <summary>The line's ecosystem has no change since its last promotion (P4), so it was left as it is.</summary>
    public const string Unchanged = "unchanged";
    /// <summary>The line holds changes the default branch does not (P1); nothing was pushed to it.</summary>
    public const string LineOnly = "line-only";
    /// <summary>A version asked for on the line is not one it can release (P5); nothing was pushed to it.</summary>
    public const string VersionRefused = "version-refused";
    /// <summary>The line moved during the job.</summary>
    public const string Moved = "moved";
    public const string Rejected = "rejected";

    /// <summary>The most line-only paths and commits a result lists.</summary>
    public const int MaxListed = 50;

    /// <summary>The most suspect paths checked one by one; more is refused as line-only without the copy check.</summary>
    public const int MaxSuspects = 500;

    public Task<VersioningPromoteResponse> RunAsync(PromoteJob job) => jobs.RunAsync("versioning-promote", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        // The tags of the requested versions' packages only: the "above every tag" check needs them, nothing else does.
        string[] fetch = ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            .. new[] { job.DefaultBranch }.Concat(job.Lines.Select(l => l.Branch)).Distinct(StringComparer.Ordinal).Select(b => $"+refs/heads/{b}:refs/heads/{b}"),
            .. job.Versions.Select(v => v.PackageId).Distinct(StringComparer.Ordinal).Select(id => $"+refs/tags/{VersionTag.Prefix(id)}*:refs/tags/{VersionTag.Prefix(id)}*")];
        await context.RequireAsync(fetch, job.Credential);
        context.EnsureWithinDisk();

        if ((await context.GitAsync(["merge-base", "--is-ancestor", job.Commit, "refs/heads/" + job.DefaultBranch], null)).ExitCode != 0)
            throw new JobAbortedException(JobReasons.Unplannable, $"The commit {job.Commit} is not on {job.DefaultBranch}.");
        var commitTree = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", job.Commit + "^{tree}"]), "rev-parse");

        var git = new GitCli(context.GitDirectory);
        ProjectGraph? graph = null;
        async Task<ProjectGraph> GraphAsync()
        {
            try
            {
                return graph ??= await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, job.Commit, context.Deadline), job.Settings.Ignore, context.Deadline);
            }
            catch (GraphException e)
            {
                throw new JobAbortedException(JobReasons.Unplannable, e.Message);
            }
        }

        // P5: each version to its line by its package's ecosystem; one nobody can take is reported, not placed.
        var byLine = new Dictionary<string, List<VersionTag>>(StringComparer.Ordinal);
        var unplaced = new List<PromoteVersionResult>();
        if (job.Versions.Count > 0)
        {
            var projects = await GraphAsync();
            foreach (var version in job.Versions)
            {
                if (projects.ById(version.PackageId) is not { } package)
                    unplaced.Add(new PromoteVersionResult(version.PackageId, version.Version.ToString(), $"{version.PackageId} is no package at {job.Commit[..12]}."));
                else if (Ecosystems.Of(projects, package, job.Settings.LinePins) is not { } ecosystem || job.Lines.FirstOrDefault(l => l.Ecosystem == ecosystem) is not { } line)
                {
                    // A recorded version waits for a release of its own line; only an asked one is worth a word here.
                    if (!job.Recorded.Contains(version.Name))
                        unplaced.Add(new PromoteVersionResult(version.PackageId, version.Version.ToString(), $"no line of this release releases {version.PackageId}."));
                }
                else
                    (byLine.TryGetValue(line.Branch, out var list) ? list : byLine[line.Branch] = []).Add(version);
            }
        }

        var results = new List<PromoteLineResult>();
        foreach (var line in job.Lines)
        {
            var versions = byLine.GetValueOrDefault(line.Branch) ?? [];
            var asked = versions.Any(v => !job.Recorded.Contains(v.Name));
            var tip = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", "refs/heads/" + line.Branch + "^{commit}"]), "rev-parse");
            if ((await context.GitAsync(["merge-base", "--is-ancestor", job.Commit, tip], null)).ExitCode == 0)
            {
                results.Add(!asked
                    ? new PromoteLineResult(line.Branch, Contained, tip, [], [])
                    : new PromoteLineResult(line.Branch, VersionRefused, tip, [], [],
                        $"{line.Branch} holds {job.Commit[..12]} already, so there is no promotion commit to tag; the versions wait for its next promotion."));
                continue;
            }
            var mergeBase = WriterService.Oid(await context.RequireAsync(["merge-base", tip, job.Commit]), "merge-base");

            // A recorded version that no longer fits (the line released past it since) is dropped and reported, never a
            // reason to hold the line: only a version the comment asks for refuses it.
            foreach (var recorded in versions.Where(v => job.Recorded.Contains(v.Name)).ToList())
            {
                var problems = VersionProblems([recorded], line);
                if (problems.Count == 0) problems = await AboveProblemsAsync(context, [recorded], line, null);
                if (problems.Count == 0) continue;
                versions.Remove(recorded);
                unplaced.Add(new PromoteVersionResult(recorded.PackageId, recorded.Version.ToString(), string.Join("; ", problems) + "."));
            }

            if (line.OnlyIfChanged && versions.Count == 0)
            {
                try
                {
                    var changed = await ChangedEcosystems.OfAsync(await GraphAsync(), new GitInputDiff(git), mergeBase, job.Commit, job.Settings, context.Deadline);
                    if (!changed.Contains(line.Ecosystem, StringComparer.Ordinal))
                    {
                        results.Add(new PromoteLineResult(line.Branch, Unchanged, tip, [], []));
                        continue;
                    }
                }
                catch (GraphException e)
                {
                    throw new JobAbortedException(JobReasons.Unplannable, e.Message);
                }
            }

            if (await VersionRefusalAsync(context, line, versions) is { } refusal)
            {
                results.Add(new PromoteLineResult(line.Branch, VersionRefused, tip, [], [], refusal));
                continue;
            }

            var lineOnly = await LineOnlyPathsAsync(context, mergeBase, tip, job.Commit);
            if (lineOnly.Count > 0)
            {
                results.Add(new PromoteLineResult(line.Branch, LineOnly, tip, [.. lineOnly.Take(MaxListed)], await LineCommitsAsync(context, mergeBase, tip, lineOnly)));
                continue;
            }

            var message = $"Promote {line.Branch} to {job.DefaultBranch} {job.Commit[..12]}\n\n"
                + $"The tree is {job.Commit}'s, a commit {job.DefaultBranch}'s workflow passed; the line's history stays as its first parent.\n";
            var promoted = WriterService.Oid(await context.RequireAsync(["commit-tree", commitTree, "-p", tip, "-p", job.Commit, "-F", "-"], input: message,
                author: job.Committer, committer: job.Committer), "commit-tree");
            // One atomic push: the line (leased on the checked tip) and its new tags (leased on not existing).
            List<(string Source, string Ref, string? Expected)> updates = [(promoted, "refs/heads/" + line.Branch, tip)];
            updates.AddRange(versions.Select(v => (promoted, "refs/tags/" + v.Name, (string?)null)));
            var push = await WriterService.PushAtomicAsync(context, job.CloneUrl, job.Credential, updates);
            results.Add(push switch
            {
                WriterService.Push.Done or WriterService.Push.UpToDate => new PromoteLineResult(line.Branch, Promoted, promoted, [], []) { Tags = [.. versions.Select(v => v.Name)] },
                WriterService.Push.LeaseRejected => new PromoteLineResult(line.Branch, Moved, tip, [], [], $"{line.Branch} (or one of its version tags) moved during the promotion."),
                WriterService.Push.Rejected rejected => new PromoteLineResult(line.Branch, Rejected, tip, [], [], rejected.Detail),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            });
        }
        return new VersioningPromoteResponse(JobOutcomes.Promoted, null, results, null, context.ElapsedMilliseconds) { Unplaced = unplaced };
    }, (outcome, reason, detail, ms) => new VersioningPromoteResponse(outcome, reason, [], detail, ms));

    /// <summary>
    /// Why <paramref name="versions"/> cannot go on <paramref name="line"/> (P5): a major other than the line's, two
    /// versions of one package, or a version not above every tag of its package. Null when all can.
    /// </summary>
    private static async Task<string?> VersionRefusalAsync(JobContext context, PromoteLine line, IReadOnlyList<VersionTag> versions)
        => VersionProblems(versions, line) is { Count: > 0 } early ? Sentence(early) : Sentence(await AboveProblemsAsync(context, versions, line, null));

    private static string? Sentence(IReadOnlyList<string> problems) => problems.Count == 0 ? null : "Not released: " + string.Join("; ", problems) + ".";

    /// <summary>The checks that need no tags: one version per package, the line's major.</summary>
    private static List<string> VersionProblems(IReadOnlyList<VersionTag> versions, PromoteLine line)
    {
        var problems = new List<string>();
        foreach (var group in versions.GroupBy(v => v.PackageId, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
                problems.Add($"{group.Key} is asked for at {string.Join(" and ", group.Select(v => v.Version))}");
            else if (group.Single().Version.Major != line.Major)
                problems.Add($"{group.Single().Name} does not have {line.Branch}'s major {line.Major}");
        }
        return problems;
    }

    /// <summary>Each version must be above every fetched tag of its package, <paramref name="except"/> left out.</summary>
    private static async Task<List<string>> AboveProblemsAsync(JobContext context, IReadOnlyList<VersionTag> versions, PromoteLine line, string? except)
    {
        var problems = new List<string>();
        foreach (var version in versions)
        {
            var refs = (await context.RequireAsync(["for-each-ref", "--format=%(refname:strip=2)", "refs/tags/" + VersionTag.Prefix(version.PackageId) + "*"])).StandardOutput;
            var highest = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(n => n != except)
                .Select(n => VersionTag.TryParse(n, out var t) && t.PackageId == version.PackageId ? t : null).OfType<VersionTag>()
                .MaxBy(t => t.Version);
            if (highest is not null && version.Version.CompareTo(highest.Version) <= 0)
                problems.Add($"{version.Name} is not above the package's tag {highest.Name}");
        }
        return problems;
    }

    public const string HandTagRecorded = "recorded";
    public const string HandTagRefused = "refused";
    public const string HandTagNotAPackage = "not-a-package";
    public const string HandTagMoved = "moved";

    /// <summary>
    /// A person's tag (P5): left alone when its id is no package at its commit (<c>schemas/v3</c>), else checked as a
    /// promotion would check it, against the current line of its ecosystem, and deleted either way (leased on
    /// <see cref="HandTagJob.TagObject"/>): left in place it would become reachable from the line at the next promotion
    /// and move every later version. <see cref="HandTagRecorded"/> means the server records it for that promotion.
    /// </summary>
    public Task<HandTagResponse> CheckHandTagAsync(HandTagJob job) => jobs.RunAsync("versioning-hand-tag", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        var prefix = VersionTag.Prefix(job.Tag.PackageId);
        await context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            $"+refs/tags/{prefix}*:refs/tags/{prefix}*"], job.Credential);
        context.EnsureWithinDisk();

        var name = job.Tag.Name;
        var current = await context.GitAsync(["rev-parse", "--verify", "--quiet", "refs/tags/" + name], null);
        if (current.ExitCode != 0 || current.StandardOutput.Trim() != job.TagObject)
            return new HandTagResponse(JobOutcomes.Planned, null, HandTagMoved, null, null, null, context.ElapsedMilliseconds);
        var commit = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", $"refs/tags/{name}^{{commit}}"]), "rev-parse");

        ProjectGraph graph;
        try
        {
            graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(new GitCli(context.GitDirectory), commit, context.Deadline), job.Settings.Ignore, context.Deadline);
        }
        catch (GraphException e)
        {
            throw new JobAbortedException(JobReasons.Unplannable, e.Message);
        }
        if (graph.ById(job.Tag.PackageId) is not { } package)
            return new HandTagResponse(JobOutcomes.Planned, null, HandTagNotAPackage, null, commit, null, context.ElapsedMilliseconds);

        string? refusal;
        string? branch = null;
        if (Ecosystems.Of(graph, package, job.Settings.LinePins) is not { } ecosystem || job.Lines.FirstOrDefault(l => l.Ecosystem == ecosystem) is not { } line)
            refusal = $"no current release line releases {job.Tag.PackageId}";
        else
        {
            branch = line.Branch;
            var problems = VersionProblems([job.Tag], line);
            if (problems.Count == 0) problems = await AboveProblemsAsync(context, [job.Tag], line, name);
            refusal = problems.Count == 0 ? null : string.Join("; ", problems);
        }

        var push = await WriterService.PushAsync(context, job.CloneUrl, job.Credential, null, "refs/tags/" + name, job.TagObject);
        if (push is WriterService.Push.LeaseRejected)
            return new HandTagResponse(JobOutcomes.Planned, null, HandTagMoved, null, commit, null, context.ElapsedMilliseconds);
        if (push is WriterService.Push.Rejected rejected)
            throw new JobAbortedException(JobReasons.Unplannable, $"The tag {name} could not be deleted: {rejected.Detail}");
        return new HandTagResponse(JobOutcomes.Planned, null, refusal is null ? HandTagRecorded : HandTagRefused, branch, commit, refusal, context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new HandTagResponse(outcome, reason, null, null, null, detail, ms));

    /// <summary>
    /// The paths the line changed since <paramref name="mergeBase"/> whose content neither <paramref name="commit"/> nor
    /// any default-branch commit between the two holds (P1), sorted.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> LineOnlyPathsAsync(JobContext context, string mergeBase, string tip, string commit)
    {
        var lineChanged = await NamesAsync(context, mergeBase, tip);
        if (lineChanged.Count == 0) return [];
        var differs = (await NamesAsync(context, tip, commit)).ToHashSet(StringComparer.Ordinal);
        var suspects = lineChanged.Where(differs.Contains).Order(StringComparer.Ordinal).ToList();
        if (suspects.Count > MaxSuspects) return suspects;

        var lineOnly = new List<string>();
        foreach (var path in suspects)
        {
            var onLine = await BlobAsync(context, tip, path);
            // Every content the default branch gave the path between the merge base and the commit, from --raw's new side.
            var log = await context.RequireAsync(["log", "--format=", "--raw", "--no-abbrev", "--no-renames", $"{mergeBase}..{commit}", "--", path]);
            var written = log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ', 5)).Where(f => f.Length == 5).Select(f => f[3]).ToHashSet(StringComparer.Ordinal);
            var copied = onLine is null ? written.Any(o => o.All(c => c == '0')) : written.Contains(onLine);
            if (!copied) lineOnly.Add(path);
        }
        return lineOnly;
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(JobContext context, string from, string to)
        => (await context.RequireAsync(["diff-tree", "-r", "--no-renames", "--name-only", "-z", from, to])).StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The blob at <paramref name="path"/> in <paramref name="commit"/>, or null when the path is absent there.</summary>
    private static async Task<string?> BlobAsync(JobContext context, string commit, string path)
    {
        var result = await context.GitAsync(["rev-parse", "--verify", "--quiet", $"{commit}:{path}"], null);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>The line's own commits that touched <paramref name="paths"/>, newest first, as <c>sha subject</c>.</summary>
    private static async Task<IReadOnlyList<string>> LineCommitsAsync(JobContext context, string mergeBase, string tip, IReadOnlyList<string> paths)
    {
        var log = await context.RequireAsync(["log", "--first-parent", "--no-merges", $"--max-count={MaxListed}", "--format=%H %s", $"{mergeBase}..{tip}", "--",
            .. paths.Take(MaxListed)]);
        return log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
