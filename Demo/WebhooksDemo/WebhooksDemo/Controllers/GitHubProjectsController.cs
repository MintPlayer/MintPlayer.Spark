using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;

namespace WebhooksDemo.Controllers;

[ApiController]
[Route("api/github/projects")]
[Authorize]
public partial class GitHubProjectsController : ControllerBase
{
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly IAsyncDocumentSession _session;
    [Inject] private readonly IGitHubInstallationService _installationService;
    [Options] private readonly Microsoft.Extensions.Options.IOptions<GitHubWebhooksOptions> _options;

    /// <summary>
    /// Lists GitHub Projects V2 accessible to the GitHub App installation.
    /// Uses the installation token (not user OAuth) for full project access.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListProjects()
    {
        // Use the App JWT to list installations, then installation tokens to query projects
        var appClient = await _installationService.CreateAppClientAsync();
        var installations = await appClient.GitHubApps.GetAllInstallationsForCurrent();

        var results = new List<ProjectInfo>();

        foreach (var installation in installations)
        {
            var ownerLogin = installation.Account.Login;
            var ownerType = installation.TargetType.StringValue == "Organization" ? "Organization" : "User";

            // Create an installation token for this specific installation
            var installClient = await _installationService.CreateInstallationClientAsync(installation.Id);
            var installToken = installClient.Connection.Credentials.Password;

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", installToken);
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SparkWebhooksDemo", "1.0"));

            // Query projects for this installation's owner
            var query = ownerType == "Organization"
                ? $"{{ organization(login: \"{ownerLogin}\") {{ projectsV2(first: 100) {{ nodes {{ id title number }} }} }} }}"
                : $"{{ user(login: \"{ownerLogin}\") {{ projectsV2(first: 100) {{ nodes {{ id title number }} }} }} }}";

            var response = await httpClient.PostAsJsonAsync("https://api.github.com/graphql", new { query });
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

            var ownerKey = ownerType == "Organization" ? "organization" : "user";
            if (!data.TryGetProperty(ownerKey, out var owner) || owner.ValueKind == JsonValueKind.Null) continue;
            if (!owner.TryGetProperty("projectsV2", out var projectsV2)) continue;

            foreach (var node in projectsV2.GetProperty("nodes").EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Null) continue;
                results.Add(new ProjectInfo
                {
                    Id = node.GetProperty("id").GetString() ?? "",
                    Title = node.GetProperty("title").GetString() ?? "",
                    Number = node.GetProperty("number").GetInt32(),
                    OwnerLogin = ownerLogin,
                    OwnerType = ownerType,
                });
            }
        }

        return Ok(results);
    }

    private sealed class ProjectInfo
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public int Number { get; init; }
        public string OwnerLogin { get; init; } = string.Empty;
        public string OwnerType { get; init; } = string.Empty;
    }

    /// <summary>
    /// Gets the Status field columns for a specific GitHub Project V2.
    /// </summary>
    [HttpGet("{nodeId}/columns")]
    public async Task<IActionResult> GetColumns(string nodeId)
    {
        var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(nodeId);
        return Ok(new { statusFieldId, columns });
    }

    /// <summary>
    /// Refreshes the cached columns for a stored GitHubProject entity.
    /// </summary>
    [HttpPost("{documentId}/sync-columns")]
    public async Task<IActionResult> SyncColumns(string documentId)
    {
        var project = await _session.LoadAsync<GitHubProject>(documentId);
        if (project == null)
            return NotFound();

        var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(project.NodeId);
        project.StatusFieldId = statusFieldId;
        project.Columns = columns;
        await _session.SaveChangesAsync();

        return Ok(new { statusFieldId, columns });
    }
}
