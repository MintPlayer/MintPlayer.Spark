using CodeCoverage.Entities;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>Writes <see cref="BackportAttempt"/> records (release lines recovery plan, segment B). The caller saves the session.</summary>
internal static class BackportAttempts
{
    public static async Task RecordAsync(IAsyncDocumentSession session, Repository repository, int source, string? sourceSha, string branch, string outcome,
        int? pullRequest, string? detail, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var id = BackportAttempt.DocumentId(repository.Provider, repository.GitHubId, source, branch);
        await session.StoreAsync(new BackportAttempt
        {
            Id = id, Repository = repository.Id!, Source = source, SourceSha = sourceSha, Branch = branch, Outcome = outcome, PullRequest = pullRequest,
            Detail = detail is { Length: > BackportAttempt.MaxDetail } ? detail[..BackportAttempt.MaxDetail] : detail, AttemptedAtUtc = nowUtc,
        }, id, cancellationToken);
    }
}
