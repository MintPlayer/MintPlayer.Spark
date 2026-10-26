using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>Tell the pull requests of one finished server-host versioning run what it published.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record CommentPublishedPackagesMessage
{
    /// <summary>The <see cref="VersioningRun"/> document id.</summary>
    public required string RunId { get; init; }
    public string? Conclusion { get; init; }
    public string? HtmlUrl { get; init; }
}

/// <summary>Forwards a finished versioning run onto the versioning lane, where a rate limit parks the comment rather than drops it.</summary>
public partial class VersioningRunCompletedRecipient : IRecipient<ForgeWebhookMessage<VersioningRunCompleted>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<VersioningRunCompleted> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken) is not { } repository) return;
        await messageBus.BroadcastAsync(new CommentPublishedPackagesMessage
        {
            RunId = VersioningRun.DocumentId(message.Provider, repository.GitHubId, evt.RunId, evt.RunAttempt),
            Conclusion = evt.Conclusion, HtmlUrl = evt.HtmlUrl,
        }, cancellationToken);
    }
}

/// <summary>
/// Once the publish workflow finished green, the merged pull request its commit came from gets one comment listing the
/// packages and versions the run tagged: the workflow packed and pushed every one of them (an unchanged package is not
/// in the list, it kept its version). The outcome is saved on the run, so a redelivered webhook comments nothing twice.
/// </summary>
/// <remarks>
/// Only server-host runs have a record to read; a repository on <c>mode: local</c> (Spark itself) gets no comment. A run
/// that failed or was cancelled comments nothing: what it tagged may not be on the feed. Its re-run is another attempt,
/// with its own record, which comments when it goes green.
/// </remarks>
public partial class CommentPublishedPackagesRecipient : IRecipient<CommentPublishedPackagesMessage>
{
    /// <summary>The most pull requests one commit's comment goes to; a commit is normally one merged pull request.</summary>
    public const int MaxPullRequests = 5;

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly ILogger<CommentPublishedPackagesRecipient> logger;

    public async Task HandleAsync(CommentPublishedPackagesMessage message, CancellationToken cancellationToken = default)
    {
        var run = await session.LoadAsync<VersioningRun>(message.RunId, cancellationToken);
        if (run is null || run.PublishedComment is not null) return;
        if (message.Conclusion != "success")
        {
            run.PublishedComment = "failed-run";
            await session.SaveChangesAsync(cancellationToken);
            return;
        }
        var repository = await session.LoadAsync<Repository>(run.Repository, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;

        var published = Published(run);
        var pulls = published.Count == 0 ? [] : await forge.MergedPullRequestsAsync(repository, run.Commit, run.Branch, cancellationToken);
        if (pulls.Count == 0)
        {
            run.PublishedComment = "none";
            await session.SaveChangesAsync(cancellationToken);
            return;
        }

        var body = Body(published, message.HtmlUrl);
        foreach (var number in pulls.Take(MaxPullRequests).Where(n => !run.CommentedPullRequests.Contains(n)))
        {
            await forge.CommentAsync(repository, number, body, cancellationToken);
            // Saved per comment, so a retry after a rate limit resumes after the last one posted.
            run.CommentedPullRequests.Add(number);
            await session.SaveChangesAsync(cancellationToken);
        }
        run.PublishedComment = "commented";
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Versioning: run {Run} of {Repository} named {Count} package(s) on #{Pulls}", run.Id, repository.FullName,
            published.Count, string.Join(", #", run.CommentedPullRequests));
    }

    /// <summary>The releases whose tag is in place, by package id.</summary>
    internal static List<VersioningRunRelease> Published(VersioningRun run)
        => [.. run.Releases.Where(r => r.Tag.Length > 0 && (r.TaggedByHand || r.TagOutcome is "created" or "already-present"))
            .OrderBy(r => r.Id, StringComparer.Ordinal)];

    internal static string Body(IReadOnlyList<VersioningRunRelease> published, string? htmlUrl)
    {
        var body = new StringBuilder();
        body.Append(published.Count == 1 ? "Published 1 package" : $"Published {published.Count} packages")
            .Append(htmlUrl is { Length: > 0 } url ? $" in [the publish run]({url})" : "").Append(":\n\n");
        body.Append("| Package | Version |\n|---|---|\n");
        foreach (var release in published)
            body.Append("| `").Append(release.Id).Append("` | `").Append(release.Version).Append("` |\n");
        return body.ToString();
    }
}
