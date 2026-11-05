using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>A pull request into the default branch to label and check for backport targets (#570 M8b), on the versioning lane.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record BackportTargetsMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
    public required string HeadSha { get; init; }
    public string? Title { get; init; }
    /// <summary>Opened or reopened: the only time default labels are added (D5).</summary>
    public required bool Opened { get; init; }
}

/// <summary>
/// Forwards an opened or pushed pull request into the default branch (#570 M8b). Only a point-load here, on the shared
/// forge-event lane; the backport branches the app opens itself target lines and are skipped, and so are bots' pull
/// requests: a dependency update on the default branch is no backport, since each line gets its own (D13).
/// </summary>
public partial class BackportTargetsEventsRecipient : IRecipient<ForgeWebhookMessage<PullRequestUpdated>>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestUpdated> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (evt.AuthorIsBot || evt.HeadRef.StartsWith(BackportRecipient.HeadPrefix, StringComparison.Ordinal)) return;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null || repository.Archived || repository.DefaultBranch != evt.BaseRef) return;

        await messageBus.BroadcastAsync(new BackportTargetsMessage
        {
            RepositoryId = evt.RepositoryId, Number = evt.Number, HeadSha = evt.HeadSha, Title = evt.Title, Opened = evt.IsFirstOpen,
        }, cancellationToken);
    }
}

/// <summary>
/// Default backport labels and the "Backport targets" check (#570 M8b, PRD D5), where coverage.yml turns release lines on
/// (D18). The git writer names the ecosystems the pull request changes; <see cref="SupportedLines"/> says which lines
/// take backports; <see cref="BackportTargets"/> decides the rest.
/// </summary>
/// <remarks>
/// Labels are added only when the pull request opens, and only when it carries no backport label yet: a reopened pull
/// request whose labels a maintainer already chose keeps them. The check follows every head, since a check belongs to a
/// commit. Labels are never removed: after the open, they are the maintainer's.
/// </remarks>
public partial class BackportTargetsRecipient : IRecipient<BackportTargetsMessage>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IEnumerable<IForgeLabels> labellers;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly ILogger<BackportTargetsRecipient> logger;

    public async Task HandleAsync(BackportTargetsMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (repository.DefaultBranch is not { } defaultBranch) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git
            || labellers.FirstOrDefault(l => l.Provider == repository.Provider) is not { } labels)
            return;

        try
        {
            var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
            if (VersioningYml.Parse(yml).Config is not { EffectiveLines: { } pattern } config) return;
            var backport = BackportYml.Parse(yml);

            if (await EcosystemsAsync(repository, git, defaultBranch, message, config.ToSettings(), cancellationToken) is not { } ecosystems) return;
            var lines = await SupportedLines.ReadAsync(session, git, repository, pattern, backport.Section.Lines, cancellationToken);
            var current = await labels.ListPullRequestLabelsAsync(repository, message.Number, cancellationToken);
            var carried = new List<string>(current);

            if (message.Opened && !current.Any(l => BackportCommandLabel(l)))
            {
                var isFix = BackportTargets.LooksLikeFix(message.Title, current, backport.Section.FixLabels);
                foreach (var label in BackportTargets.Labels(lines, ecosystems, backport.Section.DefaultLabels, isFix))
                {
                    await labels.AddLabelAsync(repository, message.Number, label, cancellationToken);
                    carried.Add(label);
                }
            }

            await forge.PublishCheckAsync(repository, message.HeadSha, BackportTargets.Check(lines, ecosystems, carried, backport.Errors), null, cancellationToken);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Backport targets: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Backport targets of #{Number} in {Repository} failed", message.Number, repository.FullName);
        }
    }

    private static bool BackportCommandLabel(string label) => Forge.BackportCommand.FromLabel(label) is not null;

    private async Task<IReadOnlyList<string>?> EcosystemsAsync(Repository repository, IForgeMergeQueue git, string defaultBranch, BackportTargetsMessage message,
        Versioning.Releases.VersioningSettings settings, CancellationToken cancellationToken)
    {
        var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
        {
            logger.LogWarning("Backport targets: no writer access for {Repository}: {Reason}", repository.FullName, push.RefusalReason);
            return null;
        }
        try
        {
            var result = await writer.VersioningEcosystemsAsync(new GitWriterEcosystemsRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), defaultBranch,
                $"refs/pull/{message.Number.ToString(CultureInfo.InvariantCulture)}/head", message.HeadSha, settings), cancellationToken);
            if (result.Outcome == GitWriterEcosystemsResult.Planned) return result.Ecosystems ?? [];
            // A head that moved on has its own message coming.
            if (result.Outcome != "head-mismatch")
                logger.LogWarning("Backport targets: no ecosystems for #{Number} of {Repository}: {Outcome} {Reason} {Detail}",
                    message.Number, repository.FullName, result.Outcome, result.Reason, result.Detail);
            return null;
        }
        catch (GitWriterRefusedException ex)
        {
            logger.LogWarning(ex, "Backport targets: the git writer refused #{Number} of {Repository}", message.Number, repository.FullName);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Backport targets: the git writer did not answer for {Repository}", repository.FullName);
            return null;
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }
}
