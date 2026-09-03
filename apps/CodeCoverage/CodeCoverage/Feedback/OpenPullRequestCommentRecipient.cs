using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Feedback;

/// <summary>
/// Posts the placeholder coverage comment when a pull request opens, so the ask
/// is answered literally: the comment appears with the PR, not a CI run later.
/// The finalize path then edits this very comment with the real numbers.
/// <para>
/// Three gates, each of which exists to avoid a comment that would never become
/// useful: no App installation, a repository that has never had coverage, and a
/// bot-authored PR that can never upload any.
/// </para>
/// </summary>
public partial class OpenPullRequestCommentRecipient : IRecipient<OpenPullRequestCommentMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IPullRequestCommentPublisher publisher;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly ILogger<OpenPullRequestCommentRecipient> logger;

    public async Task HandleAsync(OpenPullRequestCommentMessage message, CancellationToken cancellationToken = default)
    {
        // H12: a PR that can never upload coverage must not be given a comment
        // that can never stop saying "waiting".
        if (message.AuthorIsBot)
            return;

        var repository = await session.LoadAsync<Entities.Repository>(
            Entities.Repository.DocumentId(message.RepositoryGitHubId), cancellationToken);
        if (repository is null) return;

        long? installationId = null;
        if (repository.Account is not null)
            installationId = (await session.LoadAsync<Entities.Account>(repository.Account, cancellationToken))?.InstallationId;
        if (installationId is null)
        {
            // OIDC-only repositories are a supported population, and they get
            // no check-runs either. Silent, as there.
            logger.LogDebug("No installation for {Repo}; skipping the pending coverage comment", repository.FullName);
            return;
        }

        if (!await HasCoverageHistoryAsync(repository, cancellationToken))
        {
            // H10: a repository that has never uploaded is not asking for a
            // coverage bot on every PR it opens.
            logger.LogDebug("No coverage history for {Repo}; skipping the pending coverage comment", repository.FullName);
            return;
        }

        var body = PullRequestCommentRenderer.RenderPending(repository, message.HeadSha, configuration["Coverage:BaseUrl"]);
        await publisher.PublishAsync(repository, installationId.Value, message.PullRequestNumber, message.HeadSha, body, cancellationToken);
    }

    /// <summary>
    /// Cheap first, query second: the denormalized repository total answers for
    /// every repository whose default branch has ever been measured, and only a
    /// repository that has covered nothing but side branches pays for the index
    /// query.
    /// </summary>
    private async Task<bool> HasCoverageHistoryAsync(Entities.Repository repository, CancellationToken cancellationToken)
    {
        if (repository.LatestCoverage is not null) return true;

        return await session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id && c.HasCoverage)
            .AnyAsync(cancellationToken);
    }
}
