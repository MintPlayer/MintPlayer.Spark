using System.Text.Json;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.GitWriter.Jobs;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitWriter;

/// <summary>
/// The merge queue's git writer, coverage-git-writer (docs/coverage_merge_queue_PRD.md D2, M3): the only process that
/// writes to a forge on the queue's behalf.
/// <list type="bullet">
/// <item><b>Trust boundary:</b> the internal <c>coverage-git-writer</c> network, which holds exactly this service and
/// <c>coverage-app</c>. There is no API key, as for the read-only mirror: the network is the boundary, so nothing else may
/// ever join it. Its way out is <c>coverage-git-writer-egress</c>, shared with nothing.</item>
/// <item><b>No secrets at rest:</b> a short-lived, single-repository token arrives in each request's body, lives only in
/// that job's git processes' environment (<see cref="GitRunner"/>) and is never stored, logged or put in an argument
/// (T9).</item>
/// <item><b>Hostile content (T8):</b> bare, blob-less clones per job, no checkout, no hooks, no in-repo attributes,
/// <c>transfer.fsckObjects</c> on, and the job caps of <see cref="GitWriterOptions"/>.</item>
/// <item><b>Inputs are validated</b> before they reach a path, an argument or the environment (<see cref="WireValidation"/>),
/// and the regenerate patch before it is applied (<see cref="PatchValidator"/>, T4).</item>
/// </list>
/// Listens on 8080 (the aspnet image's <c>ASPNETCORE_HTTP_PORTS</c>). Configuration: section <c>GitWriter</c>.
/// </summary>
public static class GitWriterHost
{
    /// <summary>The API's JSON: System.Text.Json web defaults (camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<GitWriterOptions>(builder.Configuration.GetSection(GitWriterOptions.Section));
        builder.Services.AddSingleton(sp => new GitRunner(sp.GetRequiredService<IOptions<GitWriterOptions>>().Value.ToRunnerOptions(), sp.GetRequiredService<ILogger<GitRunner>>()));
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddSingleton<WriterService>();
        // The conflict resolvers and validators, [Register]ed in CodeCoverage.Git (merge-queue PRD §10.3).
        builder.Services.AddMergeQueueResolution();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        // Resolve eagerly: the runner creates (and checks) the empty hooks directory and global config; the job runner
        // creates the jobs' root and refuses a slot count the container limits were not sized for.
        app.Services.GetRequiredService<GitRunner>();
        app.Services.GetRequiredService<JobRunner>();
        app.MapGitWriterEndpoints();
        return app;
    }
}
