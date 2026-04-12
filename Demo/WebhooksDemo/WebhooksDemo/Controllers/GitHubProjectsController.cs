using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
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
    [Inject] private readonly UserManager<SparkUser> _userManager;

    /// <summary>
    /// Lists the authenticated user's GitHub Projects V2,
    /// including projects from organizations the user is a member of.
    /// Uses the user's stored OAuth access token.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListProjects()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
            return Unauthorized();

        var accessToken = await _userManager.GetAuthenticationTokenAsync(user, "GitHub", "access_token");
        if (string.IsNullOrEmpty(accessToken))
            return Unauthorized("No GitHub access token available. Please re-authenticate.");

        // Use raw GraphQL to avoid Octokit.GraphQL deserialization issues with null nodes
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SparkWebhooksDemo", "1.0"));

        var graphqlQuery = new { query = "{ viewer { login projectsV2(first: 100) { nodes { id title number } } organizations(first: 100) { nodes { login projectsV2(first: 100) { nodes { id title number } } } } } }" };
        var response = await httpClient.PostAsJsonAsync("https://api.github.com/graphql", graphqlQuery);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var viewer = doc.RootElement.GetProperty("data").GetProperty("viewer");
        var viewerLogin = viewer.GetProperty("login").GetString() ?? "";

        var results = new List<ProjectInfo>();

        // User's own projects
        foreach (var node in viewer.GetProperty("projectsV2").GetProperty("nodes").EnumerateArray())
        {
            if (node.ValueKind == JsonValueKind.Null) continue;
            results.Add(new ProjectInfo
            {
                Id = node.GetProperty("id").GetString() ?? "",
                Title = node.GetProperty("title").GetString() ?? "",
                Number = node.GetProperty("number").GetInt32(),
                OwnerLogin = viewerLogin,
                OwnerType = "User",
            });
        }

        // Organization projects
        foreach (var org in viewer.GetProperty("organizations").GetProperty("nodes").EnumerateArray())
        {
            if (org.ValueKind == JsonValueKind.Null) continue;
            var orgLogin = org.GetProperty("login").GetString() ?? "";
            foreach (var node in org.GetProperty("projectsV2").GetProperty("nodes").EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Null) continue;
                results.Add(new ProjectInfo
                {
                    Id = node.GetProperty("id").GetString() ?? "",
                    Title = node.GetProperty("title").GetString() ?? "",
                    Number = node.GetProperty("number").GetInt32(),
                    OwnerLogin = orgLogin,
                    OwnerType = "Organization",
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
