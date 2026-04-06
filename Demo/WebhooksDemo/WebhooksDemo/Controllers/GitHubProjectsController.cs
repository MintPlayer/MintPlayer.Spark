using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using MintPlayer.SourceGenerators.Attributes;
using Octokit.GraphQL;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;
using Connection = Octokit.GraphQL.Connection;
using ProductHeaderValue = Octokit.GraphQL.ProductHeaderValue;

namespace WebhooksDemo.Controllers;

[ApiController]
[Route("api/github/projects")]
[Authorize]
public partial class GitHubProjectsController : ControllerBase
{
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly IAsyncDocumentSession _session;

    /// <summary>
    /// Lists the authenticated user's GitHub Projects V2.
    /// Uses the user's stored OAuth access token.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListProjects()
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (string.IsNullOrEmpty(accessToken))
            return Unauthorized("No GitHub access token available. Please re-authenticate.");

        var graphQL = new Connection(
            new ProductHeaderValue("SparkWebhooksDemo", "1.0"),
            accessToken);

        // Use raw GraphQL to avoid union type issues with IProjectV2Owner
        var rawQuery = """
            {
              "query": "query { viewer { projectsV2(first: 50) { nodes { id title number owner { ... on User { login } ... on Organization { login } } } } } }"
            }
            """;

        var json = await graphQL.Run(rawQuery);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var nodes = doc.RootElement
            .GetProperty("viewer")
            .GetProperty("projectsV2")
            .GetProperty("nodes");

        var projects = nodes.EnumerateArray().Select(p => new
        {
            Id = p.GetProperty("id").GetString(),
            Title = p.GetProperty("title").GetString(),
            Number = p.GetProperty("number").GetInt32(),
            OwnerLogin = p.TryGetProperty("owner", out var owner) && owner.TryGetProperty("login", out var login)
                ? login.GetString() : null,
        }).ToList();

        return Ok(projects);
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
