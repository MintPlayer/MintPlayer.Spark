using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;

namespace WebhooksDemo.Controllers;

/// <summary>
/// Surfaces non-secret metadata about the GitHub App so the frontend can build a
/// deep-link to the app's install page. The slug is a short, public, human-readable
/// identifier (URL bar shows <c>https://github.com/apps/&lt;slug&gt;</c>).
/// </summary>
[ApiController]
[Route("api/github/app-info")]
[AllowAnonymous]
public partial class GitHubAppInfoController : ControllerBase
{
    [Inject] private readonly IConfiguration _configuration;

    [HttpGet]
    public IActionResult Get()
    {
        var slug = _configuration["GitHub:Production:AppSlug"];
        return Ok(new AppInfo { ProductionAppSlug = slug });
    }

    public sealed class AppInfo
    {
        public string? ProductionAppSlug { get; init; }
    }
}
