using System.Text.Json;
using CodeCoverage.GitMirror.Endpoints;
using CodeCoverage.Git;
using CodeCoverage.GitMirror.Graph;
using CodeCoverage.GitMirror.Mirror;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitMirror;

/// <summary>
/// The coverage-git sidecar (<c>docs/coverage_commit_graph_PRD.md</c> D1, D3, D24): the only process that runs git.
/// <list type="bullet">
/// <item><b>Trust boundary:</b> the internal <c>coverage-git</c> network, which holds exactly this service and
/// <c>coverage-app</c> (D4, D18, D24). There is no API key: the network is the boundary, so nothing else may ever join it.</item>
/// <item><b>No secrets at rest:</b> a forge token arrives in a fetch's body, lives only in that git process's environment
/// (<see cref="GitRunner"/>) and is never stored, logged or put in an argument.</item>
/// <item><b>Inputs are validated</b> before they reach a path or an argument: provider and repository id
/// (<see cref="RepoKey"/>), https clone URLs only, refs, SHA prefixes and search text (<see cref="GraphFilter"/>).</item>
/// </list>
/// Listens on 8080 (the aspnet image's <c>ASPNETCORE_HTTP_PORTS</c>). Configuration: section <c>GitMirror</c>
/// (<see cref="GitMirrorOptions"/>).
/// </summary>
public static class GitMirrorHost
{
    /// <summary>The API's JSON: System.Text.Json web defaults (camelCase), as the contract says.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<GitMirrorOptions>(builder.Configuration.GetSection(GitMirrorOptions.Section));
        builder.Services.AddSingleton(sp => new MirrorPaths(sp.GetRequiredService<IOptions<GitMirrorOptions>>().Value.Root));
        builder.Services.AddSingleton(sp => new GitRunner(sp.GetRequiredService<IOptions<GitMirrorOptions>>().Value.ToRunnerOptions(), sp.GetRequiredService<ILogger<GitRunner>>()));
        builder.Services.AddSingleton<DiskGuard>();
        builder.Services.AddSingleton<SnapshotCache>();
        builder.Services.AddSingleton<MirrorService>();
        builder.Services.AddSingleton<GraphService>();
        builder.Services.AddHostedService<IdleReaderSweeper>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        Directory.CreateDirectory(app.Services.GetRequiredService<MirrorPaths>().Root);
        // Resolve the runner eagerly: it creates (and checks) the empty hooks directory and the empty global config.
        app.Services.GetRequiredService<GitRunner>();
        app.MapGitMirrorEndpoints();
        return app;
    }
}
