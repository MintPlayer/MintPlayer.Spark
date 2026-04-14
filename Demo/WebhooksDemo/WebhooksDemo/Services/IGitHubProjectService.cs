using WebhooksDemo.Entities;

namespace WebhooksDemo.Services;

public interface IGitHubProjectService
{
    /// <summary>Fetch Status field ID + column options for a project.</summary>
    Task<(string StatusFieldId, ProjectColumn[] Columns)> GetProjectColumnsAsync(long installationId, string projectNodeId);

    /// <summary>Move an issue to a column on a project board, adding it to the board first if needed.</summary>
    Task<bool> MoveOrAddIssueToColumnAsync(long installationId, GitHubProject project, string owner, string repo, int issueNumber, string columnOptionId, bool add = true);

    /// <summary>Move a pull request to a column on a project board, adding it to the board first if needed.</summary>
    Task<bool> MoveOrAddPullRequestToColumnAsync(long installationId, GitHubProject project, string owner, string repo, int prNumber, string columnOptionId);

    /// <summary>Get issues that a PR closes (via "Closes #123" references).</summary>
    Task<List<(string Repo, int Number)>> GetClosingIssuesAsync(long installationId, string owner, string repo, int prNumber);
}
