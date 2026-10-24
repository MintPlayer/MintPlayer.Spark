using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
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
}

public partial class BackportEventsRecipient : IRecipient<ForgeWebhookMessage<BackportRequested>>
{
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<BackportRequested> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        return messageBus.BroadcastAsync(new BackportMessage
        {
            RepositoryId = evt.RepositoryId, Number = evt.Number, Lines = evt.Lines, ActorLogin = evt.ActorLogin, Source = evt.Source,
        }, cancellationToken);
    }
}

/// <summary>
/// A backport (#153 M10, PRD D10). The writer merges the pull request's change onto every release line in memory; when
/// all apply, each line gets a <c>backport/&lt;pr&gt;-&lt;line&gt;</c> branch and a pull request, so CI on it is the compile
/// check against the line's platform. When any line conflicts nothing is pushed, and the original pull request gets a
/// comment naming the files and the commands to do it by hand.
/// </summary>
/// <remarks>
/// Who may ask: a label or comment needs write access (T1, the role read from the forge per request, as the merge queue
/// does). A request made by the labels a pull request carried as it merged needs no check: the merge needed write access.
/// Which lines exist is read from <c>versioning.releaseBranches</c> on the default branch (D17), never from the request.
/// </remarks>
public partial class BackportRecipient : IRecipient<BackportMessage>
{
    public const string HeadPrefix = "backport/";

    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
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
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Backport of #{Number} in {Repository} failed", message.Number, repository.FullName);
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

        var parsed = VersioningYml.Parse(await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken));
        if (parsed.Config is not { } config)
            return $"There is nothing to backport to: {CoverageYml.FileName} on {defaultBranch} has no versioning section.";

        var targets = new List<GitWriterBackportTarget>();
        var unknown = new List<string>();
        foreach (var line in message.Lines)
        {
            var branch = Resolve(config.EffectiveReleaseBranches, line);
            if (branch is null || string.IsNullOrEmpty(await git.GetBranchHeadAsync(repository, branch, cancellationToken)))
            {
                unknown.Add(line);
                continue;
            }
            if (branch == pull.BaseRef || targets.Any(t => t.Branch == branch)) continue;
            targets.Add(new GitWriterBackportTarget(branch, $"{HeadPrefix}{pull.Number}-{Slug(line, branch)}"));
        }
        if (unknown.Count > 0)
            return $"Nothing was backported: {string.Join(", ", unknown.Select(u => $"`{u}`"))} names no release branch "
                + $"({string.Join(", ", config.EffectiveReleaseBranches.Select(p => $"`{p}`"))}).";
        if (targets.Count == 0) return null;

        var subjects = (await git.GetPullRequestCommitsAsync(repository, pull.Number, cancellationToken))
            .Select(c => BackportSubject(c.Message)).Take(250).ToList();
        var result = await RunAsync(repository, git, pull.BaseRef, commit, pull.Number, pull.Title, subjects, targets, cancellationToken);
        return await ReportAsync(repository, forge, pull, commit, result, cancellationToken);
    }

    private async Task<GitWriterBackportResult?> RunAsync(Repository repository, IForgeMergeQueue git, string source, string commit, int number, string title,
        IReadOnlyList<string> subjects, IReadOnlyList<GitWriterBackportTarget> targets, CancellationToken cancellationToken)
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
                VersioningController.Tagger, targets), cancellationToken);
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

    private static async Task<string?> ReportAsync(Repository repository, IForgeVersioning forge, ForgePullRequestText pull, string commit,
        GitWriterBackportResult? result, CancellationToken cancellationToken)
    {
        if (result is null)
            return "The backport could not run (the git writer was unavailable); add the label again to retry.";
        if (result.Outcome == GitWriterBackportResult.Conflicted)
            return ConflictComment(commit, result);
        if (result.Outcome != GitWriterBackportResult.Backported)
            return $"The backport failed ({result.Reason ?? result.Outcome}). {result.Detail}".TrimEnd();

        var lines = new List<string>();
        foreach (var target in result.Targets ?? [])
        {
            switch (target.Push)
            {
                case GitWriterBackportTargetResult.Created:
                    var opened = await forge.OpenPullRequestAsync(repository, target.Head, target.Branch, $"[{Line(target.Branch)}] {pull.Title}",
                        $"Backport of #{pull.Number} to `{target.Branch}`.\n\nCherry-picked from {commit}. CI on this pull request is the check against the line's platform.",
                        cancellationToken);
                    lines.Add($"- `{target.Branch}`: #{opened}");
                    break;
                case GitWriterBackportTargetResult.Exists:
                    lines.Add($"- `{target.Branch}`: `{target.Head}` exists already; nothing was pushed.");
                    break;
                case GitWriterBackportTargetResult.Empty:
                    lines.Add($"- `{target.Branch}`: already holds this change.");
                    break;
                default:
                    lines.Add($"- `{target.Branch}`: the push was rejected. {target.Detail}".TrimEnd());
                    break;
            }
        }
        return lines.Count == 0 ? null : "Backports:\n\n" + string.Join('\n', lines);
    }

    /// <summary>The files per line and the commands to do it by hand; nothing was pushed (D10).</summary>
    internal static string ConflictComment(string commit, GitWriterBackportResult result)
    {
        var pick = result.Mode switch
        {
            "merge" => $"git cherry-pick -x -m 1 {commit}",
            "rebase" when result.Base is { } @base => $"git cherry-pick -x {@base}..{commit}",
            _ => $"git cherry-pick -x {commit}",
        };
        var builder = new StringBuilder("Nothing was backported: the change does not apply cleanly to every line.\n");
        foreach (var target in result.Targets ?? [])
        {
            if (target.Conflicts is not { Count: > 0 } conflicts) continue;
            builder.Append(CultureInfo.InvariantCulture, $"\n`{target.Branch}` conflicts on:\n");
            foreach (var path in conflicts.Take(50))
                builder.Append(CultureInfo.InvariantCulture, $"- `{path}`\n");
            builder.Append(CultureInfo.InvariantCulture,
                $"\nTo do it by hand:\n\n```\ngit fetch origin\ngit switch -c {target.Head} origin/{target.Branch}\n{pick}\n```\n");
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

    /// <summary>The head's suffix: <c>8.x</c> for a major, else the branch with its slashes made dashes.</summary>
    private static string Slug(string line, string branch)
        => BackportCommand.Major(line) is { } major ? $"{major}.x" : branch.Replace('/', '-');

    /// <summary>The line label in a backport's title: <c>8.x</c> for <c>release/8.x</c>, else the branch.</summary>
    private static string Line(string branch) => branch[(branch.LastIndexOf('/') + 1)..];

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
