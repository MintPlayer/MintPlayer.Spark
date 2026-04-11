using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
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

        var graphQL = new Connection(
            new ProductHeaderValue("SparkWebhooksDemo", "1.0"),
            accessToken);

        // Fetch viewer login + own projects in one query
        var viewerLogin = await graphQL.Run(
            new Query()
                .Viewer
                .Select(v => v.Login));

        var userProjects = await graphQL.Run(
            new Query()
                .Viewer
                .ProjectsV2()
                .AllPages()
                .Select(p => new ProjectInfo
                {
                    Id = p.Id.Value,
                    Title = p.Title,
                    Number = p.Number,
                    OwnerLogin = viewerLogin,
                    OwnerType = "User",
                }));

        // Fetch user's organizations
        var orgs = await graphQL.Run(
            new Query()
                .Viewer
                .Organizations()
                .AllPages()
                .Select(o => new { o.Login }));

        // Fetch projects for each organization
        var orgProjects = new List<ProjectInfo>();
        foreach (var org in orgs)
        {
            var projects = await graphQL.Run(
                new Query()
                    .Organization(org.Login)
                    .ProjectsV2()
                    .AllPages()
                    .Select(p => new ProjectInfo
                    {
                        Id = p.Id.Value,
                        Title = p.Title,
                        Number = p.Number,
                        OwnerLogin = org.Login,
                        OwnerType = "Organization",
                    }));

            orgProjects.AddRange(projects);
        }

        return Ok(userProjects.Concat(orgProjects));
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
