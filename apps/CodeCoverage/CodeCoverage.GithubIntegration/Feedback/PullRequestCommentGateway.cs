using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;

namespace CodeCoverage.Feedback;

/// <summary>One comment on a pull request, reduced to what the publisher needs.</summary>
public sealed record ExistingComment(long Id, string Body, bool AuthoredByApp);

/// <summary>
/// The three GitHub calls the sticky comment needs, and nothing else.
/// <para>
/// Narrow on purpose: <see cref="IGitHubClient"/> is enormous, and the existing
/// tests in this app fake GitHub with hand-written doubles rather than a mocking
/// framework. Three methods are trivially fakeable; the whole Octokit surface is
/// not.
/// </para>
/// </summary>
public interface IPullRequestCommentGateway
{
    Task<IReadOnlyList<ExistingComment>> ListAsync(Entities.Repository repository, long installationId, int pullRequestNumber, CancellationToken cancellationToken);
    Task<long> CreateAsync(Entities.Repository repository, long installationId, int pullRequestNumber, string body, CancellationToken cancellationToken);
    Task UpdateAsync(Entities.Repository repository, long installationId, long commentId, string body, CancellationToken cancellationToken);
}

/// <summary>
/// Octokit implementation. A pull request IS an issue for this API, which is why
/// these are Issue.Comment calls rather than PullRequest ones.
/// </summary>
[Register(typeof(IPullRequestCommentGateway), ServiceLifetime.Scoped)]
public partial class GitHubPullRequestCommentGateway : IPullRequestCommentGateway
{
    [Inject] private readonly IGitHubInstallationService installationService;

    public async Task<IReadOnlyList<ExistingComment>> ListAsync(Entities.Repository repository, long installationId, int pullRequestNumber, CancellationToken cancellationToken)
    {
        var client = await installationService.CreateInstallationClientAsync(installationId);
        var comments = await client.Issue.Comment.GetAllForIssue(repository.OwnerLogin, repository.Name, pullRequestNumber);
        return [.. comments.Select(c => new ExistingComment(
            c.Id,
            c.Body ?? string.Empty,
            // A GitHub App's comments are authored by its bot user. Measured on
            // a live comment: user.type is "Bot" for an App, "User" for a human.
            c.User?.Type is not null && c.User.Type.Value == AccountType.Bot))];
    }

    public async Task<long> CreateAsync(Entities.Repository repository, long installationId, int pullRequestNumber, string body, CancellationToken cancellationToken)
    {
        var client = await installationService.CreateInstallationClientAsync(installationId);
        var created = await client.Issue.Comment.Create(repository.OwnerLogin, repository.Name, pullRequestNumber, body);
        return created.Id;
    }

    public async Task UpdateAsync(Entities.Repository repository, long installationId, long commentId, string body, CancellationToken cancellationToken)
    {
        var client = await installationService.CreateInstallationClientAsync(installationId);
        await client.Issue.Comment.Update(repository.OwnerLogin, repository.Name, commentId, body);
    }
}
