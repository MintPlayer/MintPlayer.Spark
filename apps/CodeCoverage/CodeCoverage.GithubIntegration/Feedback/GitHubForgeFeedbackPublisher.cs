using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Raven.Client.Documents.Session;

// Octokit defines its own Repository and Account, and this file needs both vocabularies at once:
// ours to identify the document, Octokit's to talk to the API. Aliased rather than fully qualified
// at each use so the signatures still read as the interface declares them.
using Repository = CodeCoverage.Entities.Repository;
using Account = CodeCoverage.Entities.Account;

namespace CodeCoverage.Feedback;

/// <summary>
/// The GitHub implementation of <see cref="IForgeFeedbackPublisher"/>: check runs for statuses, and
/// the existing sticky-comment publisher for comments.
/// </summary>
/// <remarks>
/// GitHub is the one provider whose status primitive can carry the full output, and it is also the
/// only one with a true <see cref="EForgeOutcome.Neutral"/> — so this implementation is the lossless
/// case, and the mapping below is the reference the other providers will have to degrade from.
/// </remarks>
[Register(typeof(IForgeFeedbackPublisher), ServiceLifetime.Scoped)]
public partial class GitHubForgeFeedbackPublisher : IForgeFeedbackPublisher
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IPullRequestCommentPublisher commentPublisher;
    [Inject] private readonly IAsyncDocumentSession session;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<long> PublishStatusAsync(
        Repository repository,
        string sha,
        string name,
        ForgeVerdict verdict,
        long? existingId,
        CancellationToken cancellationToken = default)
    {
        var installationId = await RequireInstallationAsync(repository, cancellationToken);
        var client = await installationService.CreateInstallationClientAsync(installationId);

        try
        {
            return await PostCheckRunAsync(client, repository, sha, name, verdict, existingId);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Caught on the status code rather than on ForbiddenException because Octokit raises a
            // bare ApiException for some 403s — the same reason the comment publisher catches this
            // way. Translated so callers never have to know what an ApiException is.
            throw new ForgeAccessDeniedException(ex.Message);
        }
    }

    private static async Task<long> PostCheckRunAsync(
        IGitHubClient client,
        Repository repository,
        string sha,
        string name,
        ForgeVerdict verdict,
        long? existingId)
    {
        var conclusion = verdict.Outcome switch
        {
            EForgeOutcome.Success => CheckConclusion.Success,
            EForgeOutcome.Failure => CheckConclusion.Failure,
            EForgeOutcome.Neutral => CheckConclusion.Neutral,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict.Outcome, "Unmapped forge outcome."),
        };
        var output = new NewCheckRunOutput(verdict.Title, verdict.Summary);

        if (existingId is { } id)
        {
            await client.Check.Run.Update(repository.OwnerLogin, repository.Name, id, new CheckRunUpdate
            {
                Status = CheckStatus.Completed,
                Conclusion = conclusion,
                Output = output,
            });
            return id;
        }

        var created = await client.Check.Run.Create(repository.OwnerLogin, repository.Name, new NewCheckRun(name, sha)
        {
            Status = CheckStatus.Completed,
            Conclusion = conclusion,
            Output = output,
        });
        return created.Id;
    }

    public async Task PublishCommentAsync(
        Repository repository,
        int pullRequestNumber,
        string sha,
        string body,
        CancellationToken cancellationToken = default)
    {
        var installationId = await RequireInstallationAsync(repository, cancellationToken);
        await commentPublisher.PublishAsync(repository, installationId, pullRequestNumber, sha, body, cancellationToken);
    }

    /// <summary>
    /// The installation that may write to this repository.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning null, because every caller of this class has already asked
    /// <see cref="Services.IForgeClient.CheckAccessAsync"/> and been told access exists. Reaching
    /// here without an installation means that check was skipped, which is a programming error and
    /// should be loud — the alternative, a silent no-op publish, is exactly the kind of quiet
    /// failure that leaves a build looking published when nothing was posted.
    /// </remarks>
    private async Task<long> RequireInstallationAsync(Repository repository, CancellationToken cancellationToken)
    {
        var installationId = repository.Account is null
            ? null
            : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;

        return installationId
            ?? throw new InvalidOperationException(
                $"No GitHub App installation for {repository.FullName}; CheckAccessAsync should have been consulted first.");
    }
}
