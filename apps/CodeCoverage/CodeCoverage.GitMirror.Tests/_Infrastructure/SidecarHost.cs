using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CodeCoverage.Git;
using CodeCoverage.GitMirror.Mirror;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeCoverage.GitMirror.Tests._Infrastructure;

/// <summary>
/// The sidecar booted from its real <see cref="GitMirrorHost"/> on a TestServer, with its mirror root in a temp directory,
/// http clone URLs allowed (the local git server has no TLS), the free-space floor at 0 unless a test sets it, and every
/// log line and every started git process recorded.
/// </summary>
internal sealed class SidecarHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly TempDir root;

    private SidecarHost(WebApplication app, TempDir root, LogCapture logs)
    {
        this.app = app;
        this.root = root;
        Logs = logs;
        Client = app.GetTestClient();
        Runner.ProcessStarting += psi => Started.Enqueue(psi);
    }

    public HttpClient Client { get; }

    public LogCapture Logs { get; }

    /// <summary>Every git process the sidecar started, in order.</summary>
    public ConcurrentQueue<ProcessStartInfo> Started { get; } = new();

    public string Root => root.Path;

    public GitRunner Runner => app.Services.GetRequiredService<GitRunner>();

    public MirrorService Mirrors => app.Services.GetRequiredService<MirrorService>();

    public MirrorPaths Paths => app.Services.GetRequiredService<MirrorPaths>();

    public string MirrorOf(string provider, string repoId) => Paths.Mirror(new RepoKey(provider, repoId));

    public static async Task<SidecarHost> StartAsync(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var root = new TempDir("mirrors");
        var builder = GitMirrorHost.CreateBuilder([]);
        builder.WebHost.UseTestServer();
        var logs = new LogCapture();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        var values = new Dictionary<string, string?>
        {
            ["GitMirror:Root"] = root.Path,
            ["GitMirror:MinFreeBytes"] = "0",
            ["GitMirror:AllowInsecureHttp"] = "true",
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
            values[k] = v;
        builder.Configuration.AddInMemoryCollection(values);
        var app = GitMirrorHost.Build(builder);
        await app.StartAsync();
        return new SidecarHost(app, root, logs);
    }

    public async Task<FetchResponse> FetchAsync(string provider, string repoId, Uri cloneUrl, object? credential = null, object? quota = null, string? mode = null)
    {
        var response = await Client.PostAsJsonAsync($"/repos/{provider}/{repoId}/fetch", new { cloneUrl = cloneUrl.AbsoluteUri, credential, quota, mode });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FetchResponse>(GitMirrorHost.Json))!;
    }

    public async Task<JsonDocument> GraphAsync(string provider, string repoId, string query = "")
    {
        var response = await Client.GetAsync($"/repos/{provider}/{repoId}/graph{query}");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        root.Dispose();
    }
}

/// <summary>Records every formatted log message (and exception) a host writes.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
