using System.Text.RegularExpressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.GraphQL;
using Octokit.GraphQL.Core;
using Octokit.GraphQL.Model;
using WebhooksDemo.Entities;
using Connection = Octokit.GraphQL.Connection;
using ProductHeaderValue = Octokit.GraphQL.ProductHeaderValue;
using ProjectColumn = WebhooksDemo.Entities.ProjectColumn;

namespace WebhooksDemo.Services;

[Register(typeof(IGitHubProjectService), ServiceLifetime.Scoped)]
public partial class GitHubProjectService : IGitHubProjectService
{
    [Inject] private readonly IGitHubInstallationService _installationService;
    [Options] private readonly Microsoft.Extensions.Options.IOptions<GitHubWebhooksOptions> _options;
    [Inject] private readonly ILogger<GitHubProjectService> _logger;

    public async Task<(string StatusFieldId, ProjectColumn[] Columns)> GetProjectColumnsAsync(string projectNodeId)
    {
        var graphQL = await CreateGraphQLConnectionAsync();

        var fields = await graphQL.Run(
            new Query()
                .Node(new ID(projectNodeId))
                .Cast<ProjectV2>()
                .Fields()
                .AllPages()
                .Select(f => f.Switch<StatusFieldInfo?>(when => when
                    .ProjectV2SingleSelectField(ssf => new StatusFieldInfo
                    {
                        Id = ssf.Id.Value,
                        Name = ssf.Name,
                        Options = ssf.Options(null).Select(o => new ProjectColumn
                        {
                            OptionId = o.Id,
                            Name = o.Name,
                        }).ToList(),
                    }))));

        var statusField = fields
            .Where(f => f != null)
            .FirstOrDefault(f => string.Equals(f!.Name, "Status", StringComparison.OrdinalIgnoreCase));

        if (statusField == null)
            return (string.Empty, []);

        return (statusField.Id, statusField.Options.ToArray());
    }

    private sealed class StatusFieldInfo
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public List<ProjectColumn> Options { get; init; } = [];
    }

    public async Task<bool> MoveIssueToColumnAsync(
        GitHubProject project, string owner, string repo, int issueNumber, string columnOptionId)
    {
        var graphQL = await CreateGraphQLConnectionAsync();

        var itemId = await GetIssueProjectItemIdAsync(graphQL, owner, repo, issueNumber, project.NodeId);
        if (itemId == null)
        {
            // Issue is not on the board yet — add it first
            var issueNodeId = await GetIssueNodeIdAsync(graphQL, owner, repo, issueNumber);
            if (issueNodeId == null) return false;

            itemId = await AddItemToProjectAsync(graphQL, project.NodeId, issueNodeId.Value);
            if (itemId == null) return false;
        }

        await MoveToColumnAsync(graphQL, project.NodeId, project.StatusFieldId, itemId.Value, columnOptionId);
        return true;
    }

    public async Task<bool> MovePullRequestToColumnAsync(
        GitHubProject project, string owner, string repo, int prNumber, string columnOptionId)
    {
        var graphQL = await CreateGraphQLConnectionAsync();

        var itemId = await GetPullRequestProjectItemIdAsync(graphQL, owner, repo, prNumber, project.NodeId);
        if (itemId == null)
        {
            // PR is not on the board yet — add it first
            var prNodeId = await GetPullRequestNodeIdAsync(graphQL, owner, repo, prNumber);
            if (prNodeId == null) return false;

            itemId = await AddItemToProjectAsync(graphQL, project.NodeId, prNodeId.Value);
            if (itemId == null) return false;
        }

        await MoveToColumnAsync(graphQL, project.NodeId, project.StatusFieldId, itemId.Value, columnOptionId);
        return true;
    }

    public async Task<List<(string Repo, int Number)>> GetClosingIssuesAsync(
        string owner, string repo, int prNumber)
    {
        var graphQL = await CreateGraphQLConnectionAsync();

        var results = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .PullRequest(prNumber)
                .ClosingIssuesReferences()
                .AllPages()
                .Select(issue => new
                {
                    issue.Number,
                    RepoName = issue.Repository.Name,
                }));

        return results.Select(r => (r.RepoName, r.Number)).ToList();
    }

    private async Task<ID?> GetIssueProjectItemIdAsync(
        Connection graphQL, string owner, string repo, int issueNumber, string projectNodeId)
    {
        var items = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .Issue(issueNumber)
                .ProjectItems()
                .AllPages()
                .Select(x => new
                {
                    ProjectID = x.Project.Id,
                    ItemID = x.Id,
                }));

        var projectItem = items.FirstOrDefault(it => it.ProjectID.Value == projectNodeId);
        return projectItem?.ItemID;
    }

    private async Task<ID?> GetPullRequestProjectItemIdAsync(
        Connection graphQL, string owner, string repo, int prNumber, string projectNodeId)
    {
        var items = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .PullRequest(prNumber)
                .ProjectItems()
                .AllPages()
                .Select(x => new
                {
                    ProjectID = x.Project.Id,
                    ItemID = x.Id,
                }));

        var projectItem = items.FirstOrDefault(it => it.ProjectID.Value == projectNodeId);
        return projectItem?.ItemID;
    }

    private async Task<ID?> GetIssueNodeIdAsync(Connection graphQL, string owner, string repo, int issueNumber)
    {
        var result = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .Issue(issueNumber)
                .Select(i => i.Id));

        return result;
    }

    private async Task<ID?> GetPullRequestNodeIdAsync(Connection graphQL, string owner, string repo, int prNumber)
    {
        var result = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .PullRequest(prNumber)
                .Select(pr => pr.Id));

        return result;
    }

    private async Task<ID?> AddItemToProjectAsync(Connection graphQL, string projectNodeId, ID contentId)
    {
        var result = await RunCleanedUp(graphQL,
            new Mutation()
                .AddProjectV2ItemById(new AddProjectV2ItemByIdInput
                {
                    ClientMutationId = Guid.NewGuid().ToString(),
                    ProjectId = new ID(projectNodeId),
                    ContentId = contentId,
                })
                .Select(r => new { ItemId = r.Item.Id }));

        // Parse the item ID from the response JSON
        // Response format: {"data":{"addProjectV2ItemById":{"item":{"id":"..."}}}}
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data))
            root = data;

        if (root.TryGetProperty("addProjectV2ItemById", out var payload)
            && payload.TryGetProperty("itemId", out var item)
            && item.TryGetProperty("id", out var idProp))
        {
            return new ID(idProp.GetString()!);
        }

        _logger.LogWarning("Failed to parse item ID from addProjectV2ItemById response: {Response}", result);
        return null;
    }

    private async Task MoveToColumnAsync(
        Connection graphQL, string projectNodeId, string statusFieldId, ID itemId, string columnOptionId)
    {
        await RunCleanedUp(graphQL,
            new Mutation()
                .UpdateProjectV2ItemFieldValue(new UpdateProjectV2ItemFieldValueInput
                {
                    ClientMutationId = Guid.NewGuid().ToString(),
                    ProjectId = new ID(projectNodeId),
                    ItemId = itemId,
                    FieldId = new ID(statusFieldId),
                    Value = new() { SingleSelectOptionId = columnOptionId },
                })
                .Select(r => r.ClientMutationId));
    }

    private async Task<Connection> CreateGraphQLConnectionAsync()
    {
        // Use the App JWT to find the first installation, then create an installation token
        var appClient = await _installationService.CreateAppClientAsync();
        var installations = await appClient.GitHubApps.GetAllInstallationsForCurrent();
        var installation = installations.FirstOrDefault()
            ?? throw new InvalidOperationException("No GitHub App installations found.");

        var restClient = await _installationService.CreateInstallationClientAsync(installation.Id);
        var token = restClient.Connection.Credentials.Password;

        return new Connection(
            new ProductHeaderValue("SparkWebhooks", "1.0"),
            token);
    }

    /// <summary>
    /// Cleans up GraphQL mutation queries to handle null values that cause errors.
    /// </summary>
    private static async Task<string> RunCleanedUp<T>(Connection connection, IQueryableValue<T> expression)
    {
        var expr = expression.Compile().ToString() ?? string.Empty;

        // Remove null fields that cause errors even though the mutation succeeds
        expr = NullValueRegex().Replace(expr, match =>
            match.Groups["c1"]?.Value == "," && match.Groups["c2"]?.Value == "," ? "," : " ");
        expr = NewlineRegex().Replace(expr, " ");

        var query = System.Text.Json.JsonSerializer.Serialize(new { query = expr });
        return await connection.Run(query);
    }

    [GeneratedRegex(@"(?<c1>\,?)(text|number|date|singleSelectOptionId|iterationId):\s?null(?<c2>\,?)\s?")]
    private static partial Regex NullValueRegex();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex NewlineRegex();
}
