using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

/// <summary>
/// What the smee relay does with a stream: deliveries reach the processor, smee's own frames do
/// not, and neither a failing handler nor a failing connection ends the tunnel. The HTTP side is a
/// fake handler returning an SSE body, which is all <c>IHttpClientFactory</c> needs to see.
/// <para>
/// Signature fidelity is not tested here — see <see cref="SmeeSignatureFidelityTests"/>.
/// </para>
/// </summary>
public class SmeeBackgroundServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Answers each request with the next scripted response; the last one repeats.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Requests;
        public string? LastAccept;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref Requests) - 1, responses.Length - 1);
            LastAccept = request.Headers.Accept.ToString();
            return Task.FromResult(responses[index]());
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingProcessor : WebhookEventProcessor
    {
        public ConcurrentQueue<(IDictionary<string, StringValues> Headers, string Body)> Deliveries { get; } = new();

        public override ValueTask ProcessWebhookAsync(IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken = default)
        {
            Deliveries.Enqueue((headers, body));
            if (headers["x-github-event"] == "fail")
                throw new InvalidOperationException("handler failed");
            return ValueTask.CompletedTask;
        }
    }

    private static Func<HttpResponseMessage> Sse(string body)
        => () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static string Delivery(string eventName, string body)
        => $"data: {{\"x-github-event\":\"{eventName}\",\"x-hub-signature-256\":\"sha256=00\",\"body\":{body},\"timestamp\":1}}\n\n";

    private static SmeeBackgroundService Build(ScriptedHandler handler, RecordingProcessor processor, string channelUrl = "https://smee.io/test-channel")
        // Parameter order follows the source-generated constructor: [Inject] fields, then [Options].
        => new(
            new SingleClientFactory(handler),
            new ServiceCollection().AddScoped<WebhookEventProcessor>(_ => processor).BuildServiceProvider(),
            NullLogger<SmeeBackgroundService>.Instance,
            Options.Create(new SmeeOptions { ChannelUrl = channelUrl }));

    private static async Task RunAsync(SmeeBackgroundService service, Func<Task> body)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task Deliveries_reach_the_processor_and_smee_frames_do_not()
    {
        var handler = new ScriptedHandler(Sse(
            "event: ready\ndata: {}\n\n"
            + ":ping\n\n"
            + "data: not json at all\n\n"
            + Delivery("push", "{\"ref\": \"refs/heads/main\", \"n\": 1.50}")));
        var processor = new RecordingProcessor();
        var service = Build(handler, processor);

        await RunAsync(service, () => AsyncWait.UntilAsync(() => processor.Deliveries.Count >= 1, "the delivery", Timeout));

        handler.LastAccept.Should().Be("text/event-stream");
        processor.Deliveries.Should().ContainSingle();
        var (headers, body) = processor.Deliveries.First();
        headers["x-github-event"].ToString().Should().Be("push");
        headers.Should().NotContainKey("timestamp");
        body.Should().Be("{\"ref\":\"refs/heads/main\",\"n\":1.50}");
    }

    [Fact]
    public async Task A_failing_handler_does_not_end_the_stream()
    {
        var handler = new ScriptedHandler(Sse(Delivery("fail", "{\"n\":1}") + Delivery("push", "{\"n\":2}")));
        var processor = new RecordingProcessor();
        var service = Build(handler, processor);

        await RunAsync(service, () => AsyncWait.UntilAsync(() => processor.Deliveries.Count >= 2, "both deliveries", Timeout));

        processor.Deliveries.Select(d => d.Body).Should().Equal("{\"n\":1}", "{\"n\":2}");
        handler.Requests.Should().Be(1, "the second delivery arrived on the same stream");
    }

    /// <summary>
    /// A refused connection and a stream smee ended are both reconnect cases, and stopping during
    /// the reconnect wait must end the service quietly.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Stopping_during_the_reconnect_wait_ends_the_service_cleanly(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(() => new HttpResponseMessage(status)
        {
            Content = new StringContent("", Encoding.UTF8, "text/event-stream"),
        });
        var service = Build(handler, new RecordingProcessor());

        await RunAsync(service, () => AsyncWait.UntilAsync(() => handler.Requests >= 1, "the first attempt", Timeout));

        service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task No_channel_means_no_connection()
    {
        var handler = new ScriptedHandler(Sse(""));
        var service = Build(handler, new RecordingProcessor(), channelUrl: "");

        await RunAsync(service, () => service.ExecuteTask!.WaitAsync(Timeout));

        handler.Requests.Should().Be(0);
    }
}
