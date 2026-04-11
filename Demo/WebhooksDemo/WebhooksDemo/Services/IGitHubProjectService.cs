using WebhooksDemo.Entities;

namespace WebhooksDemo.Services;

public interface IGitHubProjectService
{
    /// <summary>Fetch Status field ID + column options for a project.</summary>
    Task<(string StatusFieldId, ProjectColumn[] Columns)> GetProjectColumnsAsync(string projectNodeId);

    /// <summary>Move an issue to a column on a project board.</summary>
    Task<bool> MoveIssueToColumnAsync(GitHubProject project, string owner, string repo, int issueNumber, string columnOptionId);

    /// <summary>Move a pull request to a column on a project board.</summary>
    Task<bool> MovePullRequestToColumnAsync(GitHubProject project, string owner, string repo, int prNumber, string columnOptionId);

    /// <summary>Get issues that a PR closes (via "Closes #123" references).</summary>
    Task<List<(string Repo, int Number)>> GetClosingIssuesAsync(string owner, string repo, int prNumber);
}
