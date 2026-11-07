using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>A merged pull request that may be a backport (#570 M8c), on the versioning lane.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record BackportMergedMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
}

/// <summary>A person created a <c>backport/&lt;pr&gt;-&lt;line&gt;</c> branch (#570 M8c, D7), on the versioning lane.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record BackportBranchPushedMessage
{
    public required string RepositoryId { get; init; }
    public required string Branch { get; init; }
    /// <summary>The signed push's sender (T1); the pull request opens only for someone with write access.</summary>
    public required string PusherLogin { get; init; }
}

/// <summary>Forwards merges and hand-created backport branches; a filter on the shared forge-event lane, no reads.</summary>
public partial class BackportLinkEventsRecipient :
    IRecipient<ForgeWebhookMessage<PullRequestMerged>>,
    IRecipient<ForgeWebhookMessage<BranchCommitPushed>>
{
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<PullRequestMerged> message, CancellationToken cancellationToken = default)
        => messageBus.BroadcastAsync(new BackportMergedMessage { RepositoryId = message.Event.RepositoryId, Number = message.Event.Number }, cancellationToken);

    public Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        // The app's own backport branches open their pull requests themselves (after regenerating, M6); a later push to a
        // branch is no request, so a pull request someone closed is not opened again.
        if (!evt.Created || evt.PushedByBot || evt.PusherLogin is not { Length: > 0 } pusher || BackportProvenance.ParseHead(evt.Branch) is null)
            return Task.CompletedTask;
        return messageBus.BroadcastAsync(new BackportBranchPushedMessage { RepositoryId = evt.RepositoryId, Branch = evt.Branch, PusherLogin = pusher },
            cancellationToken);
    }
}

/// <summary>
/// Links backports to their source (#570 M8c, PRD §5.5, D7):
/// <list type="bullet">
/// <item>A pull request into a release line merges: the source it names gets <c>backported: &lt;line&gt;</c>. The app's
/// marker names it; a hand-made backport is recognised by a "Backport of #N" body, its <c>backport/N-…</c> head or a
/// <c>cherry-pick -x</c> trailer, and the source also gets a comment linking it.</item>
/// <item>A person creates <c>backport/&lt;pr&gt;-&lt;line&gt;</c> (the conflict comment's commands): its pull request is
/// opened into the line, with the marker.</item>
/// </list>
/// </summary>
/// <remarks>
/// The lines come from the default branch's <c>coverage.yml</c> as for every backport: the <c>versioning.lines</c>
/// pattern when lines are on, else <c>versioning.releaseBranches</c>. A source must be a pull request merged into the
/// default branch, so no text can label an arbitrary issue.
/// </remarks>
public partial class BackportLinkRecipient : IRecipient<BackportMergedMessage>, IRecipient<BackportBranchPushedMessage>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IEnumerable<IForgeLabels> labellers;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<BackportLinkRecipient> logger;

    public async Task HandleAsync(BackportMergedMessage message, CancellationToken cancellationToken = default)
    {
        // A merged backport is done: its record goes. A pull request closed without merging keeps it, since it may reopen.
        if (await session.LoadAsync<BackportPullRequest>(BackportPullRequest.DocumentId(message.RepositoryId, message.Number), cancellationToken) is { } record)
        {
            session.Delete(record);
            await session.SaveChangesAsync(cancellationToken);
        }
        if (await ContextAsync(message.RepositoryId, cancellationToken) is not { } context) return;
        var (repository, forge, defaultBranch, config) = context;
        try
        {
            if (await forge.GetPullRequestTextAsync(repository, message.Number, cancellationToken) is not { Merged: true } pull) return;
            if (pull.BaseRef == defaultBranch || !IsLine(config, pull.BaseRef)) return;

            var marked = BackportProvenance.FromMarker(pull.Body, repository.FullName);
            var source = marked ?? BackportProvenance.FromText(pull.Body, pull.HeadRef) ?? await TrailerSourceAsync(repository, forge, pull, defaultBranch, cancellationToken);
            if (source is not { } number || number == pull.Number) return;
            if (await forge.GetPullRequestTextAsync(repository, number, cancellationToken) is not { Merged: true } original || original.BaseRef != defaultBranch) return;

            var label = BackportProvenance.BackportedLabel(pull.BaseRef, config.EffectiveLines);
            if (labellers.FirstOrDefault(l => l.Provider == repository.Provider) is { } labels && !original.Labels.Contains(label))
                await labels.AddLabelAsync(repository, number, label, cancellationToken);
            if (marked is null)
                await forge.CommentAsync(repository, number, $"`{pull.BaseRef}` has this change: #{pull.Number}, backported by hand.", cancellationToken);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Backport link: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Backport link for #{Number} in {Repository} failed", message.Number, repository.FullName);
        }
    }

    public async Task HandleAsync(BackportBranchPushedMessage message, CancellationToken cancellationToken = default)
    {
        if (await ContextAsync(message.RepositoryId, cancellationToken) is not { } context) return;
        var (repository, forge, defaultBranch, config) = context;
        if (BackportProvenance.ParseHead(message.Branch) is not { } head) return;
        var line = config.EffectiveLines is { } pattern
            ? BackportRecipient.ResolveLine(pattern, head.Line)
            : BackportRecipient.Resolve(config.EffectiveReleaseBranches, head.Line);
        if (line is null || line == defaultBranch) return;
        try
        {
            // T1 as for every command: the forge's own rule (creating a branch needs write) is not relied on alone; the
            // pusher's role is read per request, and anyone below write is ignored without a word (there is no pull request
            // to answer on).
            if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git
                || await git.GetActorRoleAsync(repository, message.PusherLogin, cancellationToken) < EForgeRepositoryRole.Write)
            {
                logger.LogInformation("Backport link: {Pusher} may not open a backport in {Repository}", message.PusherLogin, repository.FullName);
                return;
            }
            if (await forge.GetPullRequestTextAsync(repository, head.Number, cancellationToken) is not { Merged: true } source || source.BaseRef != defaultBranch) return;
            var opened = await forge.OpenPullRequestAsync(repository, message.Branch, line, $"[{BackportRecipient.Line(line)}] {source.Title}",
                $"Backport of #{source.Number} to `{line}`, pushed by hand.\n\n{BackportProvenance.Marker(repository.FullName, source.Number)}",
                cancellationToken: cancellationToken);
            // A writer pushed it, so it counts as the App's own backport for the approval floor and a writer's approval.
            await BackportRegenerationEngine.RecordAsync(session, repository.Id!, opened, source.Number, line, time.GetUtcNow().UtcDateTime, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Backport link: opened #{Opened} for {Branch} of {Repository}", opened, message.Branch, repository.FullName);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Backport link: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Backport link: opening {Branch} of {Repository} failed", message.Branch, repository.FullName);
        }
    }

    private sealed record Context(Repository Repository, IForgeVersioning Forge, string DefaultBranch, VersioningConfig Config);

    private async Task<Context?> ContextAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return null;
        if (repository.DefaultBranch is not { } defaultBranch) return null;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return null;
        var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
        return VersioningYml.Parse(yml).Config is { } config ? new Context(repository, forge, defaultBranch, config) : null;
    }

    private static bool IsLine(VersioningConfig config, string branch)
        => config.EffectiveLines is { } pattern ? pattern.TryMatch(branch, out _) : ReleaseBranches.IsMatch(config.EffectiveReleaseBranches, branch, out _);

    /// <summary>The source a <c>cherry-pick -x</c> trailer names: the merged pull request into the default branch holding that commit.</summary>
    private async Task<int?> TrailerSourceAsync(Repository repository, IForgeVersioning forge, ForgePullRequestText pull, string defaultBranch, CancellationToken cancellationToken)
    {
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return null;
        var commits = await git.GetPullRequestCommitsAsync(repository, pull.Number, cancellationToken);
        foreach (var sha in BackportProvenance.CherryPickedFrom(commits.Select(c => c.Message)).Take(5))
            if ((await forge.MergedPullRequestsAsync(repository, sha, defaultBranch, cancellationToken)).FirstOrDefault() is > 0 and var number)
                return number;
        return null;
    }
}
