using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

/// <summary>
/// The developer end of the WebSocket dev tunnel, against a real WebSocket server on loopback.
/// <para>
/// No seam is needed: the cleartext guard allows <c>ws://</c> to loopback, so a Kestrel on port 0
/// is a production server as far as the client can tell. Each test scripts what the "production"
/// side sends.
/// </para>
/// </summary>
public class WebSocketDevClientServiceTests : IAsyncLifetime
{
    private const string Token = "ghp_test-token-not-real";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private WebApplication _server = null!;
    private Func<WebSocket, Task> _script = _ => Task.CompletedTask;
    private int _connections;

    public ConcurrentQueue<Handshake?> Handshakes { get; } = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.UseWebSockets();
        _server.Map("/ws", async (HttpContext context) =>
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            Interlocked.Increment(ref _connections);
            Handshakes.Enqueue(await ws.ReadObject<Handshake>());
            await _script(ws);
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Uri Url
    {
        get
        {
            var address = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Uri(address.Replace("http://", "ws://") + "/ws");
        }
    }

    /// <summary>Records every delivery; an event named <c>fail</c> throws, as a broken handler would.</summary>
    private sealed class RecordingProcessor : WebhookEventProcessor
    {
        public ConcurrentQueue<(IDictionary<string, StringValues> Headers, string Body)> Deliveries { get; } = new();

        public override ValueTask ProcessWebhookAsync(IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken = default)
        {
            Deliveries.Enqueue((headers, body));
            if (headers.TryGetValue("X-GitHub-Event", out var name) && name == "fail")
                throw new InvalidOperationException("handler failed");
            return ValueTask.CompletedTask;
        }
    }

    private WebSocketDevClientService NewClient(RecordingProcessor processor, string? url = null)
    {
        var services = new ServiceCollection()
            .AddScoped<WebhookEventProcessor>(_ => processor)
            .BuildServiceProvider();

        // Parameter order follows the source-generated constructor: [Inject] fields, then [Options].
        return new WebSocketDevClientService(
            services,
            NullLogger<WebSocketDevClientService>.Instance,
            Options.Create(new WebSocketDevTunnelOptions { ProductionWebSocketUrl = url ?? Url.ToString(), GitHubToken = Token }));
    }

    private static async Task RunAsync(WebSocketDevClientService client, Func<Task> body)
    {
        await client.StartAsync(CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await client.StopAsync(CancellationToken.None);
            client.Dispose();
        }
    }

    [Fact]
    public async Task No_url_means_the_tunnel_never_starts()
    {
        var client = NewClient(new RecordingProcessor(), url: "");

        await RunAsync(client, () => client.ExecuteTask!.WaitAsync(Timeout));

        client.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _connections.Should().Be(0);
    }

    /// <summary>
    /// One connection carries every delivery, and nothing a single delivery contains can end it:
    /// not a malformed frame, not a handler that throws, and not a header repeated under the same
    /// name. The last one used to: <c>ToDictionary</c> threw on the duplicate key outside the
    /// per-message <c>try</c>, which dropped the connection — and every delivery sent while it
    /// reconnected five seconds later.
    /// </summary>
    [Fact]
    public async Task Deliveries_survive_malformed_frames_failing_handlers_and_repeated_headers()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _script = async ws =>
        {
            await ws.WriteMessage("no separator in this one");
            await ws.WriteMessage("X-GitHub-Event: push\nX-Seen-By: proxy-a\nX-Seen-By: proxy-b\nnot-a-header\n\n{\"n\":1}");
            await ws.WriteMessage("X-GitHub-Event: fail\n\n{\"n\":2}");
            await ws.WriteMessage("X-GitHub-Event: push\n\n{\"n\":3}");
            await done.Task.WaitAsync(Timeout);
        };
        var processor = new RecordingProcessor();
        var client = NewClient(processor);

        await RunAsync(client, async () =>
        {
            await AsyncWait.UntilAsync(() => processor.Deliveries.Count >= 3, "three deliveries on one connection", Timeout);
            done.SetResult();
        });

        Handshakes.Should().ContainSingle();
        Handshakes.First()!.GithubToken.Should().Be(Token);
        _connections.Should().Be(1, "nothing in a delivery may cost the connection");
        processor.Deliveries.Select(d => d.Body).Should().Equal("{\"n\":1}", "{\"n\":2}", "{\"n\":3}");
        var first = processor.Deliveries.First().Headers;
        first["X-Seen-By"].ToArray().Should().Equal("proxy-a", "proxy-b");
        first.Should().NotContainKey("not-a-header");
    }

    /// <summary>
    /// A closed connection must release its socket before the client reconnects. The
    /// <c>ClientWebSocket</c> was never disposed, so each reconnect left the previous one alive:
    /// the server's side of the close never completed, and a long-running dev session accumulated
    /// one half-closed connection per blip.
    /// </summary>
    [Fact]
    public async Task A_socket_the_server_closed_is_released_by_the_client()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _script = async ws =>
        {
            if (Volatile.Read(ref _connections) > 1)
                return; // the reconnect: not what this test is about

            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            try
            {
                // Completes when the client acknowledges the close or tears the connection down —
                // and never, if it simply abandons the socket.
                await ws.ReceiveAsync(new ArraySegment<byte>(new byte[16]), CancellationToken.None);
            }
            catch (Exception)
            {
                // Torn down rather than acknowledged: released all the same.
            }
            released.TrySetResult();
        };
        var client = NewClient(new RecordingProcessor());

        await RunAsync(client, () => released.Task.WaitAsync(Timeout));

        released.Task.IsCompletedSuccessfully.Should().BeTrue();
    }
}
