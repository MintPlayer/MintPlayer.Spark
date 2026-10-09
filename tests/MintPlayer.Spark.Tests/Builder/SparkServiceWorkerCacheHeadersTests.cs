using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// PWA (#464, D8): the service-worker files and the web app manifest are served with
/// <c>Cache-Control: no-cache</c>, and nothing else is touched.
/// </summary>
/// <remarks>
/// The pipeline is assembled from the registered <see cref="IStartupFilter"/>s exactly as the web
/// host assembles it (see <see cref="SparkForwardedHeadersTests"/>). The terminal middleware stands
/// in for the static-file middleware: it writes its own long-lived <c>Cache-Control</c> and starts
/// the response, which is what fires the <c>OnStarting</c> callbacks.
/// </remarks>
public class SparkServiceWorkerCacheHeadersTests
{
    private const string StaticFileCacheControl = "public, max-age=31536000";

    private static RequestDelegate Pipeline()
    {
        var services = new ServiceCollection();
        services.AddSparkServiceWorkerCacheHeaders();

        var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        Action<IApplicationBuilder> configure = a => a.Run(async context =>
        {
            context.Response.Headers.CacheControl = StaticFileCacheControl;
            await context.Response.StartAsync();
        });
        foreach (var filter in provider.GetServices<IStartupFilter>().Reverse())
            configure = filter.Configure(configure);

        configure(app);
        return app.Build();
    }

    private static async Task<HttpContext> SendAsync(string path)
    {
        var context = new DefaultHttpContext();
        var feature = new StartingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);
        context.Features.Set<IHttpResponseBodyFeature>(feature);
        context.Request.Method = "GET";
        context.Request.Path = path;

        await Pipeline()(context);
        feature.HasStarted.Should().BeTrue("the terminal middleware started the response");
        return context;
    }

    [Theory]
    [InlineData("/ngsw-worker.js")]
    [InlineData("/ngsw.json")]
    [InlineData("/safety-worker.js")]
    [InlineData("/worker-basic.min.js")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/NGSW.JSON")]
    public async Task Service_worker_files_are_served_no_cache(string path)
    {
        var context = await SendAsync(path);

        context.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/main-ABC123.js")]
    [InlineData("/styles-ABC123.css")]
    [InlineData("/assets/ngsw.json")]
    [InlineData("/ngsw.json.bak")]
    [InlineData("/spark/auth/me")]
    public async Task Other_paths_keep_their_own_cache_control(string path)
    {
        var context = await SendAsync(path);

        context.Response.Headers.CacheControl.ToString().Should().Be(StaticFileCacheControl);
    }

    [Fact]
    public void AddSpark_registers_the_filter()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "libs", "spark", "MintPlayer.Spark", "SparkMiddleware.cs"));

        source.Should().Contain("services.AddSparkServiceWorkerCacheHeaders();");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "nx.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root (nx.json) not found.");
    }

    /// <summary>
    /// <see cref="DefaultHttpContext"/>'s own response feature never runs <c>OnStarting</c>
    /// callbacks; this one runs them when the response starts, like a server does.
    /// </summary>
    private sealed class StartingResponseFeature : HttpResponseFeature, IHttpResponseBodyFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];
        private bool _started;

        public override bool HasStarted => _started;

        public override void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public Stream Stream { get; } = new MemoryStream();
        public System.IO.Pipelines.PipeWriter Writer => System.IO.Pipelines.PipeWriter.Create(Stream);
        public void DisableBuffering() { }
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync() => StartAsync();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_started) return;
            // ASP.NET Core runs OnStarting callbacks in reverse registration order.
            for (var i = _onStarting.Count - 1; i >= 0; i--)
                await _onStarting[i].Callback(_onStarting[i].State);
            _started = true;
        }
    }
}
