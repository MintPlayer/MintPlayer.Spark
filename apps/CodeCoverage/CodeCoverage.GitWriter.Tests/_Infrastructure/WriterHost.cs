using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using CodeCoverage.Git;
using CodeCoverage.GitWriter.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeCoverage.GitWriter.Tests._Infrastructure;

/// <summary>
/// The writer booted from its real <see cref="GitWriterHost"/> on a TestServer: its jobs' root in a temp directory, http
/// remotes allowed (the local git server has no TLS; production accepts https only), the free-space floor at 0 unless a
/// test sets it, every log line and every started git process recorded.
/// </summary>
internal sealed class WriterHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly TempDir root;

    private WriterHost(WebApplication app, TempDir root, LogCapture logs)
    {
        this.app = app;
        this.root = root;
        Logs = logs;
        Client = app.GetTestClient();
        Client.Timeout = TimeSpan.FromMinutes(3);
        Runner.ProcessStarting += psi => Started.Enqueue(psi);
    }

    public HttpClient Client { get; }

    public LogCapture Logs { get; }

    public ConcurrentQueue<ProcessStartInfo> Started { get; } = new();

    public string Root => root.Path;

    public GitRunner Runner => app.Services.GetRequiredService<GitRunner>();

    public static async Task<WriterHost> StartAsync(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var root = new TempDir("jobs");
        var builder = GitWriterHost.CreateBuilder([]);
        builder.WebHost.UseTestServer();
        var logs = new LogCapture();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        var values = new Dictionary<string, string?>
        {
            ["GitWriter:Root"] = root.Path,
            ["GitWriter:MinFreeBytes"] = "0",
            ["GitWriter:AllowInsecureHttp"] = "true",
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
            values[k] = v;
        builder.Configuration.AddInMemoryCollection(values);
        var app = GitWriterHost.Build(builder);
        await app.StartAsync();
        return new WriterHost(app, root, logs);
    }

    public async Task<PrepareResponse> PrepareAsync(object request)
    {
        var response = await Client.PostAsJsonAsync("/jobs/prepare", request, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrepareResponse>(GitWriterHost.Json))!;
    }

    public async Task<QueueBranchPushResponse> PushQueueBranchAsync(object request)
    {
        var response = await Client.PostAsJsonAsync("/jobs/queue-branch", request, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<QueueBranchPushResponse>(GitWriterHost.Json))!;
    }

    public async Task<PublishResponse> PublishAsync(object request)
    {
        var response = await Client.PostAsJsonAsync("/jobs/publish", request, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PublishResponse>(GitWriterHost.Json))!;
    }

    public async Task<DeleteQueueBranchResponse> DeleteQueueBranchAsync(object request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, "/jobs/queue-branch") { Content = JsonContent.Create(request, options: GitWriterHost.Json) };
        var response = await Client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeleteQueueBranchResponse>(GitWriterHost.Json))!;
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
