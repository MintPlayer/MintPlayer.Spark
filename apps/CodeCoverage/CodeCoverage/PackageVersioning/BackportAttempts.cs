using CodeCoverage.Entities;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// Writes <see cref="BackportAttempt"/> records (release lines recovery plan, segment B). The caller saves the session.
/// Every outcome passes here, so it also raises <see cref="BackportAttempted"/> (#691 D41), in the same session.
/// </summary>
internal static class BackportAttempts
{
    public static async Task RecordAsync(IAsyncDocumentSession session, Webhooks.CoverageWebhookPublisher webhooks, Repository repository, int source, string? sourceSha,
        string branch, string outcome, int? pullRequest, string? detail, DateTime nowUtc, CancellationToken cancellationToken, string? logId = null)
    {
        var id = BackportAttempt.DocumentId(repository.Provider, repository.GitHubId, source, branch);
        // Keyed by the line, the outcome and the pull request: a rerun of the same step sends nothing twice.
        await webhooks.PublishAsync(new BackportAttempted(repository.FullName, repository.Provider.ToString().ToLowerInvariant(), source, sourceSha, branch, outcome,
            pullRequest, detail is { Length: > BackportAttempt.MaxDetail } ? detail[..BackportAttempt.MaxDetail] : detail,
            new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))), repository, $"{id}/{outcome}/{pullRequest}", session, cancellationToken);
        await session.StoreAsync(new BackportAttempt
        {
            Id = id, Repository = repository.Id!, Source = source, SourceSha = sourceSha, Branch = branch, Outcome = outcome, PullRequest = pullRequest,
            Detail = detail is { Length: > BackportAttempt.MaxDetail } ? detail[..BackportAttempt.MaxDetail] : detail, AttemptedAtUtc = nowUtc,
            LogId = logId,
        }, id, cancellationToken);
    }
}
