using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>A <c>/release</c> to run (segment P), queued so a slow writer never holds the webhook.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record ReleaseMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
    public required ReleaseCommentRequest Request { get; init; }
    public required string ActorLogin { get; init; }
}

public partial class ReleaseEventsRecipient : IRecipient<ForgeWebhookMessage<ReleaseRequested>>
{
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<ReleaseRequested> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        return messageBus.BroadcastAsync(new ReleaseMessage
        {
            RepositoryId = evt.RepositoryId, Number = evt.Number, Request = evt.Request, ActorLogin = evt.ActorLogin,
        }, cancellationToken);
    }
}

/// <summary>
/// A promotion of release lines (segment P, <c>docs/release_lines_recovery_plan.md</c> §2b): <c>/release</c> moves the
/// current lines of a repository with <c>versioning.lines.current: promote</c> to a green default-branch commit.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Who (P3): the commenter's role, read from the forge per request, against the repository's release role.</item>
/// <item>Which commit (P2): the newest default-branch commit whose <c>versioning.lines.workflow</c> run concluded
/// <c>success</c>, or the one the comment names when its run did.</item>
/// <item>Which lines (P4): the current lines whose ecosystem changed, every current line with <c>all</c>, or the named
/// ones. A past line is never promoted: it takes backports.</item>
/// <item>Everything else is the git writer's (<c>PromoteService</c>): the line-only check (P1), the commit, the push.</item>
/// </list>
/// The configuration is read from the default branch's <c>coverage.yml</c>, never from the request.
/// </remarks>
public partial class ReleaseRecipient : IRecipient<ReleaseMessage>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<ReleaseRecipient> logger;

    public async Task HandleAsync(ReleaseMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return;
        if (repository.DefaultBranch is not { } defaultBranch) return;

        try
        {
            if (await RunAsync(repository, git, defaultBranch, message, cancellationToken) is { } answer)
                await forge.CommentAsync(repository, message.Number, answer, cancellationToken);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Release: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Release on #{Number} in {Repository} failed", message.Number, repository.FullName);
        }
    }

    /// <summary>Runs the promotion; returns the comment to post.</summary>
    private async Task<string?> RunAsync(Repository repository, IForgeMergeQueue git, string defaultBranch, ReleaseMessage message, CancellationToken cancellationToken)
    {
        var account = repository.Account is null ? null : await session.LoadAsync<Account>(repository.Account, cancellationToken);
        var releaseRole = Repository.ResolveReleaseRole(repository, account);
        if (!Repository.MayRelease(await git.GetActorRoleAsync(repository, message.ActorLogin, cancellationToken), releaseRole))
            return $"A release needs the {RoleName(releaseRole)} role in this repository; @{message.ActorLogin} does not have it.";

        var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
        var parsed = VersioningYml.Parse(yml);
        if (parsed.Config is not { Lines: { } linesConfig } config || config.EffectiveLines is not { } pattern)
            return $"There is nothing to release: {CoverageYml.FileName} on {defaultBranch} has no `versioning.lines` section.";
        if (!linesConfig.PromotesCurrent || linesConfig.Workflow is not { } workflow)
            return $"`/release` promotes release lines only with `versioning.lines.current: {VersioningLinesConfig.Promote}` in {CoverageYml.FileName} on {defaultBranch}; "
                + "this repository's lines take backports.";

        // P0: the current lines, the highest major of each ecosystem among the existing line branches.
        var branches = await git.ListBranchesAsync(repository, SupportedLines.BranchPrefix(pattern), cancellationToken);
        var current = pattern.CurrentLines(branches);
        if (current.Count == 0)
            return $"There is nothing to release: no branch matches `{pattern.Text}`.";
        if (Select(message.Request, pattern, current) is not { } selected)
            return Refusal(message.Request, pattern, current);

        // P5: the comment's versions (each refused when a registry has it), then the recorded ones still waiting.
        var versions = new List<VersionTag>();
        foreach (var asked in message.Request.Versions)
        {
            if (!VersionTag.TryParse(asked.PackageId + VersionTag.Separator + asked.Version, out var tag))
                return $"Nothing was released: `{asked.PackageId}@{asked.Version}` is not a package id and version that can be tagged.";
            switch (await PublishedVersions.ExistsAsync(httpClients.CreateClient(), tag.PackageId, tag.Version.ToString(), cancellationToken))
            {
                case true: return $"Nothing was released: {tag.PackageId} {tag.Version} is published already.";
                case null: return "Nothing was released: the package registries could not be asked whether the versions are published already; comment `/release` again to retry.";
            }
            versions.Add(tag);
        }
        var pending = (await session.Advanced.LoadStartingWithAsync<ReleaseVersion>(ReleaseVersion.Prefix(repository.Id!), pageSize: 256, token: cancellationToken))
            .Where(r => r.PlacedCommit is null).ToList();
        var recorded = new Dictionary<string, ReleaseVersion>(StringComparer.Ordinal);
        foreach (var record in pending.OrderBy(r => r.RecordedUtc))
            if (!versions.Any(v => v.PackageId == record.PackageId) && VersionTag.TryParse(record.Tag, out var tag))
            {
                versions.Add(tag);
                recorded[tag.Name] = record;
            }

        var runs = await git.ListWorkflowRunsAsync(repository, workflow, defaultBranch, cancellationToken);
        if (Green(runs, message.Request.Sha) is not { } green)
            return message.Request.Sha is { } sha
                ? $"Nothing was released: `{sha}` has no successful `{workflow}` run among the latest on {defaultBranch}. Name a green commit, or leave the sha out for the newest one."
                : $"Nothing was released: none of the latest `{workflow}` runs on {defaultBranch} succeeded.";

        var push = await git.GetWriterAccessAsync(repository, null, pushesWorkflowChanges: true, cancellationToken);
        if (push.Access is not { } access)
        {
            logger.LogWarning("Release: no writer access for {Repository}: {Reason}", repository.FullName, push.RefusalReason);
            return "The release could not run (the App has no push access to the repository right now); comment `/release` again to retry.";
        }
        GitWriterPromoteResult result;
        try
        {
            var onlyIfChanged = message.Request is { Lines.Count: 0, All: false };
            result = await writer.VersioningPromoteAsync(new GitWriterPromoteRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), defaultBranch, green.HeadSha, VersioningController.Tagger,
                [.. selected.Select(l => new GitWriterPromoteLine(pattern.Format(l), l.Ecosystem, l.Major, onlyIfChanged))], config.ToSettings(),
                [.. versions.Select(v => new GitWriterPromoteVersion(v.PackageId, v.Version.ToString(), recorded.ContainsKey(v.Name)))]), cancellationToken);
        }
        catch (Exception ex) when (ex is GitWriterRefusedException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Release: the git writer failed for {Repository}", repository.FullName);
            return "The release could not run (the git writer was unavailable); comment `/release` again to retry.";
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }

        // What the line's versioning step asks about: each tag pushed, recorded as placed on its promotion commit.
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var line in (result.Lines ?? []).Where(l => l.Outcome == GitWriterPromoteLineResult.Promoted))
            foreach (var name in line.Tags ?? [])
            {
                if (!VersionTag.TryParse(name, out var tag)) continue;
                await HandTagRecipient.RecordAsync(session, repository.Id!, tag, message.ActorLogin, ReleaseVersion.FromComment, now, cancellationToken);
                if (await session.LoadAsync<ReleaseVersion>(ReleaseVersion.DocumentId(repository.Id!, name), cancellationToken) is { } record)
                    (record.PlacedCommit, record.PlacedUtc) = (line.Commit, now);
            }
        // A recorded version the writer could not place no longer fits (no package, or its line released past it):
        // it is reported once in the comment and forgotten, so it never holds a later release.
        foreach (var stale in result.Unplaced ?? [])
            if (recorded.GetValueOrDefault(stale.PackageId + VersionTag.Separator + stale.Version) is { } record)
                session.Delete(record);
        await session.SaveChangesAsync(cancellationToken);
        return Report(repository, defaultBranch, workflow, green, runs, result);
    }

    /// <summary>The lines to promote, or null when the request names one that is no current line.</summary>
    internal static IReadOnlyList<ReleaseLine>? Select(ReleaseCommentRequest request, ReleaseLinePattern pattern, IReadOnlyList<ReleaseLine> current)
    {
        if (request.All || request.Lines.Count == 0) return current;
        var selected = new List<ReleaseLine>();
        foreach (var name in request.Lines)
        {
            if (Named(pattern, name) is not { } line || !current.Contains(line)) return null;
            if (!selected.Contains(line)) selected.Add(line);
        }
        return selected;
    }

    private static string Refusal(ReleaseCommentRequest request, ReleaseLinePattern pattern, IReadOnlyList<ReleaseLine> current)
    {
        var bad = request.Lines.Where(n => Named(pattern, n) is not { } l || !current.Contains(l)).Select(n => $"`{n}`");
        return $"Nothing was released: {string.Join(", ", bad)} names no current release line. The current lines are "
            + string.Join(", ", current.Select(l => $"`{l}`")) + "; an older line takes backports instead.";
    }

    private static ReleaseLine? Named(ReleaseLinePattern pattern, string name)
        => ReleaseLine.TryParse(name) is { } line ? line : pattern.TryMatch(name, out var matched) ? matched : null;

    /// <summary>
    /// The newest successful run (P2), or the successful run of the commit the request names (a prefix of its sha).
    /// The forge lists runs newest first.
    /// </summary>
    internal static ForgeWorkflowRun? Green(IReadOnlyList<ForgeWorkflowRun> runs, string? sha)
        => runs.FirstOrDefault(r => r.IsCompleted && r.Conclusion == "success"
            && (sha is null || r.HeadSha.StartsWith(sha, StringComparison.OrdinalIgnoreCase)));

    internal static string Report(Repository repository, string defaultBranch, string workflow, ForgeWorkflowRun green, IReadOnlyList<ForgeWorkflowRun> runs,
        GitWriterPromoteResult result)
    {
        if (result.Outcome != GitWriterPromoteResult.Promoted)
            return $"The release failed ({result.Reason ?? result.Outcome}). {result.Detail}".TrimEnd();

        var text = new StringBuilder();
        var link = $"https://github.com/{repository.FullName}/commit/{green.HeadSha}";
        text.Append(CultureInfo.InvariantCulture, $"Release of [{green.HeadSha[..12]}]({link}) on {defaultBranch} ([`{workflow}` passed]({green.HtmlUrl})):\n\n");
        foreach (var line in result.Lines ?? [])
        {
            text.Append(line.Outcome switch
            {
                GitWriterPromoteLineResult.Promoted when line.Tags is { Count: > 0 } tags
                    => $"- `{line.Branch}`: promoted ({line.Commit[..12]}) with {string.Join(", ", tags.Select(t => $"`{t}`"))}; its release run starts now.",
                GitWriterPromoteLineResult.Promoted => $"- `{line.Branch}`: promoted ({line.Commit[..12]}); its release run starts now.",
                GitWriterPromoteLineResult.VersionRefused => $"- `{line.Branch}`: **not promoted**. {line.Detail}".TrimEnd(),
                GitWriterPromoteLineResult.Contained => $"- `{line.Branch}`: holds this commit already.",
                GitWriterPromoteLineResult.Unchanged => $"- `{line.Branch}`: left as it is, nothing in its ecosystem changed (`/release {Line(line.Branch)}` moves it anyway).",
                GitWriterPromoteLineResult.LineOnly => $"- `{line.Branch}`: **not promoted**: it holds changes {defaultBranch} does not, which a promotion would drop.",
                GitWriterPromoteLineResult.Moved => $"- `{line.Branch}`: moved while the promotion ran; nothing was pushed.",
                _ => $"- `{line.Branch}`: the push was rejected. {line.Detail}".TrimEnd(),
            });
            text.Append('\n');
        }
        foreach (var line in (result.Lines ?? []).Where(l => l.Outcome == GitWriterPromoteLineResult.LineOnly))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n**`{line.Branch}` only** (land these on {defaultBranch} first, or revert them on the line):\n\n");
            foreach (var path in line.Paths ?? []) text.Append(CultureInfo.InvariantCulture, $"- `{path}`\n");
            if (line.Commits is { Count: > 0 } commits)
            {
                text.Append("\nThe line's commits that touched them:\n\n");
                foreach (var commit in commits) text.Append(CultureInfo.InvariantCulture, $"- {commit}\n");
            }
        }
        foreach (var version in result.Unplaced ?? [])
            text.Append(CultureInfo.InvariantCulture, $"\n`{version.PackageId}@{version.Version}` was not placed: {version.Detail}\n");
        // P2: what was left out, the default branch's newer runs that have not passed (yet).
        // A named older sha leaves newer green commits out by choice; those are not "not passed".
        var newer = runs.TakeWhile(r => r.Id != green.Id).GroupBy(r => r.HeadSha, StringComparer.Ordinal)
            .Count(g => g.Key != green.HeadSha && !g.Any(r => r.IsCompleted && r.Conclusion == "success"));
        if (newer > 0)
            text.Append(CultureInfo.InvariantCulture, $"\n{newer} newer commit{(newer == 1 ? "" : "s")} on {defaultBranch} {(newer == 1 ? "is" : "are")} left out: "
                + $"{(newer == 1 ? "its" : "their")} `{workflow}` run has not passed.\n");
        return text.ToString().TrimEnd();
    }

    private static string Line(string branch) => branch[(branch.LastIndexOf('/') + 1)..];

    private static string RoleName(LookupReferences.EReleaseRole role) => role switch
    {
        LookupReferences.EReleaseRole.Write => "write",
        LookupReferences.EReleaseRole.Admin => "admin",
        _ => "maintain",
    };
}
