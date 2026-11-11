using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Git.Backports;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>A backport to run (#153 M10, D10), queued so a slow writer never holds the webhook.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record BackportMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public required string ActorLogin { get; init; }
    public required EBackportSource Source { get; init; }
    /// <summary><c>/backport &lt;line&gt; --draft</c> (#570 D7): a conflicting line gets a draft, whatever <c>onConflict</c> says.</summary>
    public bool Draft { get; init; }
}

public partial class BackportEventsRecipient : IRecipient<ForgeWebhookMessage<BackportRequested>>
{
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<BackportRequested> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        return messageBus.BroadcastAsync(new BackportMessage
        {
            RepositoryId = evt.RepositoryId, Number = evt.Number, Lines = evt.Lines, ActorLogin = evt.ActorLogin, Source = evt.Source, Draft = evt.Draft,
        }, cancellationToken);
    }
}

/// <summary>
/// A backport (#153 M10, PRD D10). The writer merges the pull request's change onto every release line in memory, after
/// normalizing its platform values to the line's (#570 S2); each line that applies gets a
/// <c>backport/&lt;pr&gt;-&lt;line&gt;</c> branch and a pull request, so CI on it is the compile check against the line's
/// platform. Lines are independent (#570 D6): a line that conflicts gets nothing pushed, and the original pull request's
/// comment names its files, the platform values the change edits there, and the commands to do it by hand.
/// </summary>
/// <remarks>
/// Who may ask: a label or comment needs write access (T1, the role read from the forge per request, as the merge queue
/// does). A request made by the labels a pull request carried as it merged needs no check: the merge needed write access.
/// Which lines exist is read from the default branch's <c>coverage.yml</c> (D17), never from the request: the
/// <c>versioning.lines</c> pattern when release lines are on (#568 D15; <c>backport: dotnet-10.x</c>), else
/// <c>versioning.releaseBranches</c>.
/// </remarks>
public partial class BackportRecipient : IRecipient<BackportMessage>
{
    public const string HeadPrefix = "backport/";

    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IBackportRegenerationEngine regenerations;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<BackportRecipient> logger;

    public async Task HandleAsync(BackportMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return;
        if (repository.DefaultBranch is not { } defaultBranch) return;

        try
        {
            if (await RefusalAsync(repository, forge, git, defaultBranch, message, cancellationToken) is { } refusal)
            {
                await forge.CommentAsync(repository, message.Number, refusal, cancellationToken);
                return;
            }
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Backport: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
            await FailedAsync(repository, message, $"the App is missing the {ex.RequiredPermission} permission", cancellationToken);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Backport of #{Number} in {Repository} failed", message.Number, repository.FullName);
            await FailedAsync(repository, message, $"the forge answered an error: {ex.Message}", cancellationToken);
        }
    }

    /// <summary>
    /// Segment B: a backport the forge failed is recorded for every line asked for (under the name it was asked by, since
    /// the line may not have been resolved), so it shows on the repository page although no comment could be posted.
    /// </summary>
    private async Task FailedAsync(Repository repository, BackportMessage message, string detail, CancellationToken cancellationToken)
    {
        try
        {
            using var fresh = session.Advanced.DocumentStore.OpenAsyncSession();
            foreach (var line in message.Lines.Distinct(StringComparer.Ordinal))
                await BackportAttempts.RecordAsync(fresh, repository, message.Number, null, line, BackportAttempt.Failed, null, detail, time.GetUtcNow().UtcDateTime,
                    cancellationToken);
            await fresh.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Backport: could not record the failure of #{Number} in {Repository}", message.Number, repository.FullName);
        }
    }

    /// <summary>Runs the backport; returns the comment to post, or null when there is nothing to say.</summary>
    private async Task<string?> RefusalAsync(Repository repository, IForgeVersioning forge, IForgeMergeQueue git, string defaultBranch, BackportMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Source != EBackportSource.Merge
            && await git.GetActorRoleAsync(repository, message.ActorLogin, cancellationToken) < EForgeRepositoryRole.Write)
            return $"A backport needs write access to the repository; @{message.ActorLogin} does not have it.";

        if (await forge.GetPullRequestTextAsync(repository, message.Number, cancellationToken) is not { } pull) return null;
        if (!pull.Merged || pull.MergeCommitSha is not { } commit)
            return message.Source == EBackportSource.Comment
                ? $"This pull request has not merged. Add the label `backport: {message.Lines[0]}` and the backport runs when it merges."
                : null;

        var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
        var parsed = VersioningYml.Parse(yml);
        if (parsed.Config is not { } config)
            return $"There is nothing to backport to: {CoverageYml.FileName} on {defaultBranch} has no versioning section.";
        var queue = QueueSection(yml, defaultBranch);
        var rules = AutoResolveRules(queue);
        // #570 D9, M6: derived files keep the line's side and are regenerated by its workflow; not under `never`.
        var regeneratedPaths = LineSidePaths(queue);
        // §5.8 backport:, from the default branch as well; a broken section is reported and its defaults apply.
        var backport = BackportYml.Parse(yml);
        var draft = message.Draft || backport.Section.OnConflict == BackportYml.Draft;

        var targets = new List<GitWriterBackportTarget>();
        var unknown = new List<string>();
        foreach (var line in message.Lines)
        {
            var branch = config.EffectiveLines is { } linePattern ? ResolveLine(linePattern, line) : Resolve(config.EffectiveReleaseBranches, line);
            if (branch is null || string.IsNullOrEmpty(await git.GetBranchHeadAsync(repository, branch, cancellationToken)))
            {
                unknown.Add(line);
                continue;
            }
            if (branch == pull.BaseRef || targets.Any(t => t.Branch == branch)) continue;
            targets.Add(new GitWriterBackportTarget(branch, $"{HeadPrefix}{pull.Number}-{Slug(line, branch, config.EffectiveLines)}", rules, draft, regeneratedPaths));
        }
        if (unknown.Count > 0)
            return $"Nothing was backported: {string.Join(", ", unknown.Select(u => $"`{u}`"))} names no release branch "
                + (config.EffectiveLines is { } lines ? $"(`{lines.Text}`)." : $"({string.Join(", ", config.EffectiveReleaseBranches.Select(p => $"`{p}`"))}).");
        // Segment P (P0): a current line that is promoted takes no backports; the change reaches it with the next `/release`.
        if (config is { Lines.PromotesCurrent: true, EffectiveLines: { } promotePattern } && targets.Count > 0)
        {
            var current = promotePattern.CurrentLines(await git.ListBranchesAsync(repository, SupportedLines.BranchPrefix(promotePattern), cancellationToken))
                .Select(promotePattern.Format).ToHashSet(StringComparer.Ordinal);
            var promoted = targets.Where(t => current.Contains(t.Branch)).Select(t => t.Branch).ToList();
            targets.RemoveAll(t => current.Contains(t.Branch));
            if (promoted.Count > 0 && targets.Count == 0)
                return message.Source == EBackportSource.Merge ? null
                    : $"Nothing was backported: {string.Join(", ", promoted.Select(p => $"`{p}`"))} {(promoted.Count == 1 ? "is a current line" : "are current lines")}, "
                        + "promoted with `/release` instead of backported. The change reaches it with the next release.";
        }
        if (targets.Count == 0) return null;

        var subjects = (await git.GetPullRequestCommitsAsync(repository, pull.Number, cancellationToken))
            .Select(c => BackportSubject(c.Message)).Take(250).ToList();
        var result = await RunAsync(repository, git, pull.BaseRef, commit, pull.Number, pull.Title, subjects, targets, backport.Section.Markers, cancellationToken);
        var report = await ReportAsync(repository, forge, pull, commit, [.. targets.Select(t => t.Branch)], result, queue, backport.Section, cancellationToken);
        if (backport.Errors.Count == 0)
            return report;
        var errors = $"The `backport:` section of {CoverageYml.FileName} on {defaultBranch} has errors, so its defaults were used:\n"
            + string.Join('\n', backport.Errors.Take(10).Select(e => $"- {e}"));
        return report is null ? errors : report + "\n\n" + errors;
    }

    private async Task<GitWriterBackportResult?> RunAsync(Repository repository, IForgeMergeQueue git, string source, string commit, int number, string title,
        IReadOnlyList<string> subjects, IReadOnlyList<GitWriterBackportTarget> targets, PlatformMarkerOptions markers, CancellationToken cancellationToken)
    {
        var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
        {
            logger.LogWarning("Backport: no writer access for {Repository}: {Reason}", repository.FullName, push.RefusalReason);
            return null;
        }
        try
        {
            return await writer.VersioningBackportAsync(new GitWriterBackportRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), source, commit, number, Title(title), subjects,
                VersioningController.Tagger, targets, markers), cancellationToken);
        }
        catch (GitWriterRefusedException ex)
        {
            logger.LogWarning(ex, "Backport: the git writer refused #{Number} of {Repository}", number, repository.FullName);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Backport: the git writer did not answer for {Repository}", repository.FullName);
            return null;
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }

    private async Task<string?> ReportAsync(Repository repository, IForgeVersioning forge, ForgePullRequestText pull, string commit, IReadOnlyList<string> targets,
        GitWriterBackportResult? result, MergeQueueSection? queue, BackportSection backport, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        // Segment B: every line asked for ends in a record, whatever the comment says.
        Task Attempt(string branch, string outcome, int? opened = null, string? detail = null)
            => BackportAttempts.RecordAsync(session, repository, pull.Number, commit, branch, outcome, opened, detail, now, cancellationToken);

        if (result is null || result.Outcome != GitWriterBackportResult.Backported)
        {
            var failure = result is null
                ? "The backport could not run (the git writer was unavailable); add the label again to retry."
                : $"The backport failed ({result.Reason ?? result.Outcome}). {result.Detail}".TrimEnd();
            foreach (var branch in targets)
                await Attempt(branch, BackportAttempt.Failed, detail: failure);
            await session.SaveChangesAsync(cancellationToken);
            return failure;
        }

        // Each line has its own outcome (#570 D6): a conflict on one holds back no other.
        var lines = new List<string>();
        foreach (var target in result.Targets ?? [])
        {
            switch (target.Push)
            {
                case GitWriterBackportTargetResult.Created:
                    var title = $"[{Line(target.Branch)}] {pull.Title}";
                    var body = BackportProvenance.WithMarker(
                        $"Backport of #{pull.Number} to `{target.Branch}`.\n\nCherry-picked from {commit}. CI on this pull request is the check against the line's platform."
                        + PlatformNote(target) + ResolutionNote(target), repository.FullName, pull.Number);
                    if (target.Regenerate is { Count: > 0 } regenerate)
                    {
                        // #570 D9, M6: the pull request opens once the line's workflow regenerated these files.
                        var plan = BackportRegenerationEngine.Plan(queue, target);
                        if (plan.Refusal is null && target.Commit is { } backportCommit)
                        {
                            await Attempt(target.Branch, BackportAttempt.Regenerating, detail: $"{target.Head} waits for {string.Join(", ", plan.Workflows)}");
                            await regenerations.BeginAsync(repository, new BackportRegeneration
                            {
                                Head = target.Head, Branch = target.Branch, Number = pull.Number, Title = title, Body = body, Sha = backportCommit, SourceSha = commit,
                                Workflows = [.. plan.Workflows], RegeneratedPaths = [.. plan.Globs], Paths = [.. regenerate],
                                PostRebaseTimeoutMinutes = queue!.PostRebaseTimeoutMinutes, Enqueue = Enqueues(backport, queue, target.Branch),
                            }, cancellationToken);
                            lines.Add($"- `{target.Branch}`: `{target.Head}` is pushed; its pull request opens once {string.Join(", ", plan.Workflows.Select(w => $"`{w}`"))} "
                                + $"regenerated {regenerate.Count} file{(regenerate.Count == 1 ? "" : "s")} on the line");
                            break;
                        }
                        body += BackportRegenerationEngine.NotRegeneratedNote(regenerate, plan.Refusal ?? "the git writer named no backport commit");
                    }
                    var opened = await forge.OpenPullRequestAsync(repository, target.Head, target.Branch, title, body, cancellationToken: cancellationToken);
                    // #570 M7, D12: a backport with stale regenerated files waits for a person.
                    var enqueue = target.Regenerate is not { Count: > 0 } && Enqueues(backport, queue, target.Branch);
                    await BackportRegenerationEngine.RecordAsync(session, repository.Id!, opened, pull.Number, commit, target.Branch, now, cancellationToken);
                    await (target.Regenerate is { Count: > 0 } notRegenerated
                        ? Attempt(target.Branch, BackportAttempt.NotRegenerated, opened, $"{notRegenerated.Count} regenerated file(s) are stale")
                        : Attempt(target.Branch, BackportAttempt.Opened, opened));
                    if (enqueue)
                        await BackportRegenerationEngine.EnqueueAsync(outbox, session, repository.Id!, opened, cancellationToken);
                    await session.SaveChangesAsync(cancellationToken);
                    lines.Add((target.Resolutions is { Count: > 0 } resolved
                        ? $"- `{target.Branch}`: #{opened}, with {resolved.Count} conflict{(resolved.Count == 1 ? "" : "s")} resolved automatically"
                        : $"- `{target.Branch}`: #{opened}") + (enqueue ? ", handed to the merge queue" : ""));
                    break;
                case GitWriterBackportTargetResult.Exists:
                    await Attempt(target.Branch, BackportAttempt.BranchExists, detail: target.Head);
                    lines.Add($"- `{target.Branch}`: `{target.Head}` exists already; nothing was pushed.");
                    break;
                case GitWriterBackportTargetResult.Empty:
                    await Attempt(target.Branch, BackportAttempt.AlreadyOnLine);
                    lines.Add($"- `{target.Branch}`: already holds this change.");
                    break;
                case GitWriterBackportTargetResult.Conflicted:
                    await Attempt(target.Branch, BackportAttempt.Conflicted, detail: string.Join(", ", (target.Conflicts ?? []).Take(50)));
                    lines.Add($"- `{target.Branch}`: conflicts; nothing was pushed (see below).");
                    break;
                case GitWriterBackportTargetResult.Drafted:
                    // D7 draft: the conflict markers are committed; NoConflictMarkers keeps the pull request red until resolved.
                    var draft = await forge.OpenPullRequestAsync(repository, target.Head, target.Branch, $"[{Line(target.Branch)}] {pull.Title}",
                        BackportProvenance.WithMarker(
                            $"Backport of #{pull.Number} to `{target.Branch}`.\n\nCherry-picked from {commit}. **It conflicts**: git's conflict markers are committed "
                            + "in the files below. Resolve them on this branch, then mark the pull request ready.\n\n"
                            + string.Join('\n', (target.Conflicts ?? []).Take(50).Select(p => $"- `{p}`")) + PlatformNote(target)
                            + (target.Regenerate is { Count: > 0 } stale ? BackportRegenerationEngine.NotRegeneratedNote(stale, "the backport conflicts") : ""),
                            repository.FullName, pull.Number),
                        draft: true, cancellationToken: cancellationToken);
                    // Once someone resolves the conflicts, a writer's approval queues it like any other backport.
                    await BackportRegenerationEngine.RecordAsync(session, repository.Id!, draft, pull.Number, commit, target.Branch, now, cancellationToken);
                    await Attempt(target.Branch, BackportAttempt.Drafted, draft, string.Join(", ", (target.Conflicts ?? []).Take(50)));
                    await session.SaveChangesAsync(cancellationToken);
                    lines.Add($"- `{target.Branch}`: #{draft}, a draft with {target.Conflicts?.Count ?? 0} conflicting file{(target.Conflicts?.Count == 1 ? "" : "s")} to resolve");
                    break;
                default:
                    await Attempt(target.Branch, BackportAttempt.Failed, detail: $"the push was rejected. {target.Detail}".TrimEnd());
                    lines.Add($"- `{target.Branch}`: the push was rejected. {target.Detail}".TrimEnd());
                    break;
            }
        }
        // A line the writer did not answer for is lost otherwise.
        foreach (var branch in targets.Where(b => result.Targets?.Any(t => t.Branch == b) != true))
        {
            await Attempt(branch, BackportAttempt.Failed, detail: "the git writer did not report this line");
            lines.Add($"- `{branch}`: the git writer did not report this line; add the label again to retry.");
        }
        await session.SaveChangesAsync(cancellationToken);
        if (lines.Count == 0)
            return null;
        // #570 D11: whatever each line's outcome, a first-party package the change raised above the line's major is named
        // with the upstream release it probably needs.
        var diagnoses = new StringBuilder();
        foreach (var target in result.Targets ?? [])
            diagnoses.Append(await UpstreamReleases.DiagnoseAsync(session, forge, repository, pull.BaseRef, target.Branch, target.Raises, cancellationToken));
        return "Backports:\n\n" + string.Join('\n', lines) + ConflictSections(commit, result) + diagnoses;
    }

    /// <summary>
    /// The merge queue's <c>autoResolve</c> rules for a backport's conflicts (#570 M5, PRD §5.3 step 2), from the
    /// <b>default branch's</b> <c>coverage.yml</c> like everything else here. The line's own copy is never read: merge-queue
    /// O16's reason holds for lines too, since a widened allow-list pushed to a branch must not govern merges into it. A
    /// section with errors gives no rules, so every conflict is reported rather than resolved by a half-read list.
    /// </summary>
    internal static IReadOnlyList<GitWriterAutoResolve> AutoResolveRules(string? yml, string defaultBranch)
        => AutoResolveRules(QueueSection(yml, defaultBranch));

    private static IReadOnlyList<GitWriterAutoResolve> AutoResolveRules(MergeQueueSection? section)
        => section is null ? [] : [.. section.AutoResolve.Select(GitWriterAutoResolve.From)];

    /// <summary>
    /// The paths a backport never merges (#570 D9, M6): those of the whole-file <c>take-base</c> rules that regenerate, the
    /// derived files (lockfiles, <c>modelHashes.json</c>). The change's copy is reset to its parent's, so the merge keeps the
    /// line's, and the line's workflow regenerates it. Null under <c>postRebaseWhen: never</c> or without such a rule.
    /// </summary>
    /// <remarks>
    /// Not the whole of <c>regeneratedPaths</c> (owner, 2026-11-07): that list names what a regenerate patch may touch,
    /// and holds files with hand-written content as well (Spark's <c>Model/*.json</c>, <c>translations.json</c>,
    /// <c>security.json</c>). Resetting those dropped the change's own edits, which no regeneration brings back: #580's
    /// backports lost HR's add-new flags and two translation keys. They merge as text like any other file, under their
    /// own rules (keep-both for JSON).
    /// </remarks>
    internal static IReadOnlyList<string>? LineSidePaths(MergeQueueSection? queue)
    {
        if (queue is null or { PostRebaseWhen: EPostRebaseWhen.Never }) return null;
        List<string> paths = [.. queue.AutoResolve
            .Where(r => r is { Strategy: EMergeQueueResolveStrategy.TakeBase, Keys.Count: 0 } && r.Regenerate != false)
            .Select(r => r.Path)
            .Distinct(StringComparer.Ordinal)];
        return paths.Count > 0 ? paths : null;
    }

    /// <summary>
    /// Whether a backport into <paramref name="branch"/> goes to the merge queue (#570 M7, PRD D12): <c>backport.enqueue</c>
    /// (default true), a queue that is on, and a line its <c>branches</c> name. A draft never is; the queue refuses drafts.
    /// </summary>
    internal static bool Enqueues(BackportSection backport, MergeQueueSection? queue, string branch)
        => backport.Enqueue && queue is { Enabled: true } && queue.Queues(branch);

    /// <summary>The default branch's <c>mergeQueue</c> section, or null when it has errors (or there is none).</summary>
    private static MergeQueueSection? QueueSection(string? yml, string defaultBranch)
        => MergeQueueYml.Parse(yml, defaultBranch) is { Section: { } section, Errors.Count: 0 } ? section : null;

    /// <summary>Per conflicting line: the files, the platform values the change edits there, and the commands to do it
    /// by hand. Empty when no line conflicts.</summary>
    internal static string ConflictSections(string commit, GitWriterBackportResult result)
    {
        var pick = result.Mode switch
        {
            "merge" => $"git cherry-pick -x -m 1 {commit}",
            "rebase" when result.Base is { } @base => $"git cherry-pick -x {@base}..{commit}",
            _ => $"git cherry-pick -x {commit}",
        };
        var builder = new StringBuilder();
        foreach (var target in result.Targets ?? [])
        {
            if (target.Push != GitWriterBackportTargetResult.Conflicted) continue;
            builder.Append(CultureInfo.InvariantCulture, $"\n\n`{target.Branch}` conflicts on:\n");
            foreach (var path in (target.Conflicts ?? []).Take(50))
                builder.Append(CultureInfo.InvariantCulture, $"- `{path}`\n");
            if (target.Detail is { Length: > 0 } why)
                builder.Append(CultureInfo.InvariantCulture, $"\n{why}\n");
            builder.Append(PlatformNote(target));
            builder.Append(CultureInfo.InvariantCulture,
                $"\nTo do it by hand:\n\n```\ngit fetch origin\ngit switch -c {target.Head} origin/{target.Branch}\n{pick}\n```\n\n");
            // #570 M8c opens the pull request of a pushed backport branch; M8d's option drafts this line instead.
            builder.Append(CultureInfo.InvariantCulture,
                $"Push the branch and its pull request opens, or comment `/backport {BackportProvenance.ParseHead(target.Head)?.Line ?? target.Branch} --draft` for a draft pull request with the conflict markers committed.");
        }
        return builder.ToString();
    }

    /// <summary>Each conflict the merge queue's rules resolved on the line (PRD §5.3 step 4), and which of them still
    /// need the line's regenerate workflow: until M6 dispatches it, the pull request's CI is the check.</summary>
    internal static string ResolutionNote(GitWriterBackportTargetResult target)
    {
        if (target.Resolutions is not { Count: > 0 } resolutions)
            return "";
        var builder = new StringBuilder("\n\nConflicts resolved by the `mergeQueue.autoResolve` rules (the line's side is stage 2):\n");
        foreach (var resolution in resolutions.Take(50))
            builder.Append(CultureInfo.InvariantCulture, $"- `{resolution.Path}`: {resolution.Strategy}{(resolution.Regenerate ? ", needs regenerating" : "")}\n");
        return builder.ToString();
    }

    /// <summary>The platform values the change edits on this line's own platform (#570 D8's safety rule): never
    /// normalized away, so the reader knows why a marker line conflicts or changed.</summary>
    internal static string PlatformNote(GitWriterBackportTargetResult target)
    {
        if (target.PlatformChanges is not { Count: > 0 } changes)
            return "";
        var builder = new StringBuilder("\n\nThe change edits this line's platform:\n");
        foreach (var change in changes.Take(20))
        {
            if (change.Before.Length == 0)
                builder.Append(CultureInfo.InvariantCulture, $"- `{change.Path}`: adds {change.Marker} `{change.After}`\n");
            else
                builder.Append(CultureInfo.InvariantCulture, $"- `{change.Path}`: {change.Marker} `{change.Before}` → `{change.After}`\n");
        }
        return builder.ToString();
    }

    /// <summary>The branch a line names: a major or <c>N.x</c> through the first single-star pattern (as a cut names
    /// it, D12), or a branch name that matches a pattern. Null when it names none.</summary>
    internal static string? Resolve(IReadOnlyList<string> patterns, string line)
    {
        if (BackportCommand.Major(line) is { } major)
            return ReleaseBranchCuts.NameFor(patterns, major);
        return ReleaseBranches.IsMatch(patterns, line, out _) ? line : null;
    }

    /// <summary>
    /// With release lines on (#568 D15): the branch a line name (<c>dotnet-10.x</c>, what a default label names, #570 M8b)
    /// formats to, or a branch the pattern matches. A bare major names no ecosystem, so it resolves to nothing.
    /// </summary>
    internal static string? ResolveLine(ReleaseLinePattern pattern, string line)
    {
        if (ReleaseLine.TryParse(line) is { } named) return pattern.Format(named);
        return pattern.TryMatch(line, out _) ? line : null;
    }

    /// <summary>The head's suffix: <c>8.x</c> for a major, the line's name, else the branch with its slashes made dashes.</summary>
    private static string Slug(string line, string branch, ReleaseLinePattern? pattern)
        => BackportCommand.Major(line) is { } major ? $"{major}.x"
            : pattern is not null && pattern.TryMatch(branch, out var named) && named is not null ? named.ToString()
            : branch.Replace('/', '-');

    /// <summary>The line label in a backport's title: <c>8.x</c> for <c>release/8.x</c>, else the branch.</summary>
    internal static string Line(string branch) => branch[(branch.LastIndexOf('/') + 1)..];

    private static string BackportSubject(string message)
    {
        var end = message.IndexOf('\n');
        var subject = (end < 0 ? message : message[..end]).Trim();
        return subject.Length > 256 ? subject[..256] : subject.Length == 0 ? "(no subject)" : subject;
    }

    private static string Title(string title)
    {
        var clean = new string(title.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Backport" : clean.Length > 256 ? clean[..256] : clean;
    }
}
