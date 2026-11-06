#if DEBUG
using CodeCoverage.GitWriter;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// Development only: the merge queue's git writer, booted from its real <see cref="GitWriterHost"/> inside this process on
/// a TestServer, so the onboarding, merge-queue and versioning jobs work locally without the docker sidecar. Same HTTP
/// contract, same validation and hardening; no port is opened. Each job clones into
/// <see cref="MergeQueueOptions.LocalGitWriterRootOrDefault"/>/<c>{job id}</c> and deletes that directory when it ends; the
/// writer never touches anything else under the root.
/// </summary>
/// <remarks>
/// Compiled into Debug builds only (CodeCoverage.csproj): production publishes Release and writes only through the
/// <c>coverage-git-writer</c> sidecar, so the app container never holds the push code (owner, 2026-11-06). The writer
/// runs as <c>Production</c> with its own application name and an empty content root, so it reads neither this app's
/// appsettings nor its user secrets.
/// </remarks>
internal sealed class DevelopmentGitWriter(IOptions<MergeQueueOptions> options, ILogger<DevelopmentGitWriter> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? writer;

    /// <summary>A handler into the writer's TestServer. Hosted services start before the app serves requests, so every
    /// client the factory builds finds the writer running.</summary>
    public HttpMessageHandler CreateHandler()
        => (writer ?? throw new InvalidOperationException("The in-process git writer has not started.")).GetTestServer().CreateHandler();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var root = options.Value.LocalGitWriterRootOrDefault;
        var contentRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codecoverage-git-writer")).FullName;
        var builder = GitWriterHost.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GitWriterHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            ContentRootPath = contentRoot,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["GitWriter:Root"] = root });
        writer = GitWriterHost.Build(builder);
        await writer.StartAsync(cancellationToken);
        logger.LogInformation("Development: the git writer runs in-process and clones into {Root}", root);
    }

    public Task StopAsync(CancellationToken cancellationToken) => writer?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (writer is not null)
            await writer.DisposeAsync();
    }
}
#endif
