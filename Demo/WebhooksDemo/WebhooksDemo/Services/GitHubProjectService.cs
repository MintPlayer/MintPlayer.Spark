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

        // Use raw GraphQL query — the Octokit.GraphQL typed API has limited support
        // for ProjectV2FieldConfiguration union types.
        var rawQuery = $$"""
            {
              "query": "query { node(id: \"{{projectNodeId}}\") { ... on ProjectV2 { fields(first: 50) { nodes { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } }"
            }
            """;

        var json = await graphQL.Run(rawQuery);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var fieldsArray = doc.RootElement
            .GetProperty("node")
            .GetProperty("fields")
            .GetProperty("nodes");

        string statusFieldId = string.Empty;
        var columns = new List<ProjectColumn>();

        foreach (var field in fieldsArray.EnumerateArray())
        {
            // Skip nodes that didn't match the SingleSelectField fragment (they'll be empty objects)
            if (!field.TryGetProperty("name", out var nameProp))
                continue;

            var fieldName = nameProp.GetString();
            if (!string.Equals(fieldName, "Status", StringComparison.OrdinalIgnoreCase))
                continue;

            statusFieldId = field.GetProperty("id").GetString() ?? string.Empty;

            foreach (var option in field.GetProperty("options").EnumerateArray())
            {
                columns.Add(new ProjectColumn
                {
                    OptionId = option.GetProperty("id").GetString() ?? string.Empty,
                    Name = option.GetProperty("name").GetString() ?? string.Empty,
                });
            }
            break;
        }

        return (statusFieldId, columns.ToArray());
    }

    public async Task<bool> MoveIssueToColumnAsync(
        GitHubProject project, string owner, string repo, int issueNumber, string columnOptionId)
    {
        var graphQL = await CreateGraphQLConnectionAsync();

        var itemId = await GetIssueProjectItemIdAsync(graphQL, owner, repo, issueNumber, project.NodeId);
        if (itemId == null)
            return false;

        await MoveToColumnAsync(graphQL, project.NodeId, project.StatusFieldId, itemId.Value, columnOptionId);
        return true;
    }

    public async Task<bool> MovePullRequestToColumnAsync(
        GitHubProject project, string owner, string repo, int prNumber, string columnOptionId)
    {
        var graphQL = await CreateGraphQLConnectionAsync();

        var itemId = await GetPullRequestProjectItemIdAsync(graphQL, owner, repo, prNumber, project.NodeId);
        if (itemId == null)
            return false;

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
                .ClosingIssuesReferences(first: 50)
                .Nodes
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
                .ProjectItems(first: 10)
                .Nodes
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
                .ProjectItems(first: 10)
                .Nodes
                .Select(x => new
                {
                    ProjectID = x.Project.Id,
                    ItemID = x.Id,
                }));

        var projectItem = items.FirstOrDefault(it => it.ProjectID.Value == projectNodeId);
        return projectItem?.ItemID;
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
        var opts = _options.Value;
        var installationId = opts.ProductionAppId
            ?? throw new InvalidOperationException("ProductionAppId is required for GraphQL operations.");

        // Create a REST client to get the installation token, then extract it for GraphQL
        var restClient = await _installationService.CreateClientAsync(installationId);

        // Octokit stores token-based credentials in the Password field
        var token = restClient.Connection.Credentials.Password;

        return new Connection(
            new ProductHeaderValue("SparkWebhooks", "1.0"),
            token);
    }

    /// <summary>
    /// Cleans up GraphQL mutation queries to handle null values that cause errors.
    /// Pattern from ProjectDashboard's GraphQLEx helper.
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
