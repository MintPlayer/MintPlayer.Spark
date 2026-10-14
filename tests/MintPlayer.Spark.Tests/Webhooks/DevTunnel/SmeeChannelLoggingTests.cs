using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

/// <summary>
/// The smee.io channel URL is a credential: whoever has it reads every webhook delivery smee relays to it. It reached the
/// log at Information twice: the service's own "Connecting to smee.io channel: {url}", and <c>IHttpClientFactory</c>'s
/// default request logging for the service's named client ("Start processing HTTP request GET https://smee.io/…", plus
/// the same URL as a logging scope). The tunnel is registered here exactly as <c>AddSmeeDevTunnel</c> registers it, with
/// only the network replaced, and every log line and scope of every category is captured at Trace.
/// </summary>
public class SmeeChannelLoggingTests
{
    private const string Channel = "QzVJ7pT2mXr9LkWd";
    private const string ChannelUrl = "https://smee.io/" + Channel;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A delivery that ends the stream (OK), and a refused connection the service logs and retries (503).</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task The_channel_url_never_reaches_a_log(HttpStatusCode status)
    {
        var logs = new CapturingLoggerProvider();
        var processor = new RecordingProcessor();
        var network = new Smee(status);

        var options = new GitHubWebhooksOptions();
        options.AddSmeeDevTunnel(ChannelUrl);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<WebhookEventProcessor>(processor);
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => network));
        ApplyRegistrations(options, services);
        await using var provider = services.BuildServiceProvider();
        var tunnel = provider.GetServices<IHostedService>().OfType<SmeeBackgroundService>().Single();

        await tunnel.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(
                () => status == HttpStatusCode.OK
                    ? !processor.Deliveries.IsEmpty
                    : logs.Entries.Any(e => e.Category.EndsWith(nameof(SmeeBackgroundService), StringComparison.Ordinal) && e.Level == LogLevel.Warning),
                status == HttpStatusCode.OK ? "the delivery" : "the logged connection failure", Timeout);
        }
        finally
        {
            await tunnel.StopAsync(CancellationToken.None);
        }

        network.Requests.Should().BeGreaterThan(0);
        logs.Entries.Should().Contain(e => e.Category.EndsWith(nameof(SmeeBackgroundService), StringComparison.Ordinal) && e.Text.Contains("smee.io"),
            "the tunnel still says which channel it connects to, in a form that is no credential");
        logs.Entries.Where(e => e.Text.Contains(Channel, StringComparison.OrdinalIgnoreCase)).Select(e => $"{e.Category}: {e.Text}")
            .Should().BeEmpty();
    }

    /// <summary>smee.io as the tunnel sees it: one delivery and the end of the stream, or a refusal.</summary>
    private sealed class Smee(HttpStatusCode status) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var body = status == HttpStatusCode.OK
                ? "data: {\"x-github-event\":\"push\",\"x-hub-signature-256\":\"sha256=00\",\"body\":{\"n\":1},\"timestamp\":1}\n\n"
                : "";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class RecordingProcessor : WebhookEventProcessor
    {
        public ConcurrentQueue<string> Deliveries { get; } = new();

        public override ValueTask ProcessWebhookAsync(IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken = default)
        {
            Deliveries.Enqueue(body);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Every message, exception and scope, with its category: what any sink could print.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Text)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                provider.Entries.Enqueue((category, LogLevel.None, $"scope: {state}"));
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => provider.Entries.Enqueue((category, logLevel, $"{formatter(state, exception)} {exception}"));
        }
    }

    /// <summary>As <c>AddSparkGitHubWebhooks</c> does during composition; internal, hence reflection (see
    /// <c>GitHubWebhooksDevTunnelExtensionsTests</c>).</summary>
    private static void ApplyRegistrations(GitHubWebhooksOptions options, IServiceCollection services)
        => typeof(GitHubWebhooksOptions)
            .GetMethod("ApplyRegistrations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(options, [services]);
}
