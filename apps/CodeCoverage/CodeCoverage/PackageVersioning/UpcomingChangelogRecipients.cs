using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>#693 Q12: a push to a versioned line of a repository with the changelog on, whose upcoming notes to compose.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record UpcomingChangelogMessage
{
    public required string RepositoryId { get; init; }
    public required string Branch { get; init; }
    public required string Sha { get; init; }
}

/// <summary>
/// Forwards a push to the default branch, or to a line the default branch's cached patterns match, of a repository whose
/// changelog is on. Everything else is dropped here, so a feature-branch push never reaches the forge.
/// </summary>
public partial class UpcomingChangelogEventsRecipient : IRecipient<ForgeWebhookMessage<BranchCommitPushed>>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken) is not { DefaultBranch: { } defaultBranch } repository
            || repository.Connection == RepositoryConnection.Disconnected || repository.Archived)
            return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolveChangelog(repository, account).Enabled) return;
        if (evt.Branch != defaultBranch)
        {
            var main = await session.LoadAsync<UpcomingChangelog>(UpcomingChangelog.DocumentId(repository.Provider, repository.GitHubId, defaultBranch), cancellationToken);
            if (main is null) return;
            var versioned = main.LinePattern is { } pattern
                ? ReleaseLinePattern.TryParse(pattern, out _)?.TryMatch(evt.Branch, out _) == true
                : ReleaseBranches.IsMatch(main.ReleaseBranches, evt.Branch, out _);
            if (!versioned) return;
        }
        await messageBus.BroadcastOnceAsync(new UpcomingChangelogMessage { RepositoryId = evt.RepositoryId, Branch = evt.Branch, Sha = evt.Sha },
            $"upcoming-changelog:{evt.RepositoryId}:{evt.Branch}:{evt.Sha}", cancellationToken);
    }
}

/// <summary>
/// #693 Q12 (owner: "A"): composes what the next release of the pushed line would list, with the git writer's plan job, and
/// keeps it as the line's <see cref="UpcomingChangelog"/>. A plan the writer could not make leaves the last one; the next
/// push composes again.
/// </summary>
public partial class UpcomingChangelogRecipient : IRecipient<UpcomingChangelogMessage>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<UpcomingChangelogRecipient> logger;

    public async Task HandleAsync(UpcomingChangelogMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (repository.DefaultBranch is not { } defaultBranch || !writer.IsConfigured) return;
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolveChangelog(repository, account).Enabled) return;

        VersioningConfig? config;
        try
        {
            config = VersioningYml.Parse(await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken)).Config;
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Upcoming changelog: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
            return;
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Upcoming changelog: coverage.yml of {Repository} could not be read", repository.FullName);
            return;
        }
        if (config is null || !config.IsVersionedBranch(message.Branch, defaultBranch, out var branchMajor)) return;

        var plan = await VersioningPlans.PlanAsync(writer, git, repository, message.Branch, message.Sha, defaultBranch, branchMajor, config.ToSettings(), true,
            logger, "Upcoming changelog", cancellationToken);
        if (plan is null) return;

        var id = UpcomingChangelog.DocumentId(repository.Provider, repository.GitHubId, message.Branch);
        var upcoming = await session.LoadAsync<UpcomingChangelog>(id, cancellationToken);
        if (upcoming is null) await session.StoreAsync(upcoming = new UpcomingChangelog { Id = id }, id, cancellationToken);
        Apply(upcoming, repository.Id!, message.Branch, message.Sha, clock.GetUtcNow().UtcDateTime, plan.Upcoming ?? []);
        if (message.Branch == defaultBranch)
        {
            upcoming.LinePattern = config.EffectiveLines?.Text;
            upcoming.ReleaseBranches = [.. config.EffectiveReleaseBranches];
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    internal static void Apply(UpcomingChangelog upcoming, string repositoryId, string branch, string commit, DateTime at, IReadOnlyList<GitWriterUpcomingNotes> notes)
    {
        upcoming.Repository = repositoryId;
        upcoming.Branch = branch;
        upcoming.Commit = commit;
        upcoming.UpdatedAtUtc = at;
        upcoming.Releases = [.. notes.OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => new UpcomingChangelogRelease { Package = n.Id, Notes = n.Notes, Omitted = n.Omitted })];
    }

    /// <summary>A repository's lines, the default branch first, for the changelog page: the id prefix, no query.</summary>
    public static async Task<List<UpcomingChangelog>> ForRepositoryAsync(ISparkSession session, Repository repository, CancellationToken cancellationToken = default)
    {
        // A repository has a handful of lines; one page of the prefix holds them all.
        var lines = await session.LoadStartingWithAsync<UpcomingChangelog>(UpcomingChangelog.ForRepository(repository.Provider, repository.GitHubId), 0, 1024,
            cancellationToken);
        return [.. lines.Where(l => l.Repository == repository.Id)
            .OrderBy(l => l.Branch == repository.DefaultBranch ? 0 : 1).ThenByDescending(l => l.Branch, StringComparer.Ordinal)];
    }
}
