using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace MintPlayer.Spark.Tests.SocketExtensions;

/// <summary>
/// Exercises <see cref="System.Net.WebSockets.SocketExtensions"/> against a real
/// connected WebSocket pair served by TestServer. Each test spins up a fresh host
/// whose server-side handler decides what to send/receive; the client-side is a
/// <see cref="ClientWebSocket"/> obtained via <see cref="TestServer.CreateWebSocketClient"/>.
/// </summary>
public class SocketExtensionsTests : IAsyncLifetime
{
    private IHost _host = null!;
    private Func<WebSocket, Task> _serverHandler = _ => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Use(async (context, next) =>
                    {
                        if (context.WebSockets.IsWebSocketRequest)
                        {
                            using var ws = await context.WebSockets.AcceptWebSocketAsync();
                            await _serverHandler(ws);
                            return;
                        }
                        await next();
                    });
                }))
            .Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<WebSocket> ConnectAsync()
    {
        var wsClient = _host.GetTestServer().CreateWebSocketClient();
        return await wsClient.ConnectAsync(
            new Uri(_host.GetTestServer().BaseAddress, "/"),
            CancellationToken.None);
    }

    [Fact]
    public async Task WriteMessage_then_ReadMessage_round_trips_a_short_string()
    {
        string? receivedOnServer = null;
        _serverHandler = async ws =>
        {
            receivedOnServer = await ws.ReadMessage();
            await ws.WriteMessage("echo:" + receivedOnServer);
        };

        var client = await ConnectAsync();
        await client.WriteMessage("hello");
        var echoed = await client.ReadMessage();

        receivedOnServer.Should().Be("hello");
        echoed.Should().Be("echo:hello");
    }

    [Fact]
    public async Task ReadMessage_reassembles_a_body_that_spans_multiple_receive_frames()
    {
        // Internal buffer is 512 bytes — send something much bigger so the loop runs.
        var payload = new string('a', 2048);
        _serverHandler = async ws =>
        {
            var incoming = await ws.ReadMessage();
            await ws.WriteMessage(incoming);
        };

        var client = await ConnectAsync();
        await client.WriteMessage(payload);
        var echoed = await client.ReadMessage();

        echoed.Should().Be(payload);
        echoed.Length.Should().Be(2048);
    }

    [Fact]
    public async Task WriteMessage_splits_long_payloads_into_frames_marking_only_the_last_as_EndOfMessage()
    {
        // Record the frames the server receives from the helper's SendAsync.
        var frames = new List<(int Count, bool EndOfMessage)>();
        var done = new TaskCompletionSource();
        _serverHandler = async ws =>
        {
            var buffer = new byte[256];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                frames.Add((result.Count, result.EndOfMessage));
            } while (!result.EndOfMessage);
            done.TrySetResult();
            // Echo anything back so the client can close cleanly.
            await ws.WriteMessage("ack");
        };

        var client = await ConnectAsync();
        await client.WriteMessage(new string('x', 1300));
        await done.Task;
        _ = await client.ReadMessage();

        frames.Sum(f => f.Count).Should().Be(1300);
        frames.SkipLast(1).Should().OnlyContain(f => !f.EndOfMessage);
        frames.Last().EndOfMessage.Should().BeTrue();
        frames.Count.Should().BeGreaterThan(1, "1300 bytes cannot fit in a single non-final frame");
    }

    [Fact]
    public async Task ReadMessage_throws_WebSocketException_when_the_peer_sends_a_Close_frame()
    {
        _serverHandler = async ws =>
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        };

        var client = await ConnectAsync();
        var act = async () => await client.ReadMessage();

        await act.Should().ThrowAsync<WebSocketException>()
            .WithMessage("*closed*");
    }

    [Fact]
    public async Task WriteObject_and_ReadObject_round_trip_a_POCO_via_JSON()
    {
        Envelope? receivedOnServer = null;
        _serverHandler = async ws =>
        {
            receivedOnServer = await ws.ReadObject<Envelope>();
            await ws.WriteObject(new Envelope { Type = "ack", Value = receivedOnServer?.Value ?? 0 });
        };

        var client = await ConnectAsync();
        await client.WriteObject(new Envelope { Type = "ping", Value = 42 });
        var reply = await client.ReadObject<Envelope>();

        receivedOnServer!.Type.Should().Be("ping");
        receivedOnServer.Value.Should().Be(42);
        reply!.Type.Should().Be("ack");
        reply.Value.Should().Be(42);
    }

    [Fact]
    public async Task ReadObject_returns_null_when_the_peer_sent_the_JSON_literal_null()
    {
        _serverHandler = async ws =>
        {
            await ws.WriteMessage("null");
        };

        var client = await ConnectAsync();
        var reply = await client.ReadObject<Envelope>();

        reply.Should().BeNull();
    }

    private sealed class Envelope
    {
        public string Type { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
