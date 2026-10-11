using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>A test client for <c>/spark/live</c>: sends operations, reads frames (pings skipped), and proves silence.</summary>
internal sealed class LiveTestClient : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly List<JsonElement> pending = [];
    private Task<WebSocketReceiveResult>? receiving;
    private readonly byte[] buffer = new byte[256 * 1024];
    private readonly MemoryStream message = new();

    private LiveTestClient(WebSocket socket) => this.socket = socket;

    public WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
    public WebSocketState State => socket.State;
    public int FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }

    public static async Task<LiveTestClient> ConnectAsync(WebSocketClient client, Uri baseAddress, Action<HttpRequest>? configure = null)
    {
        if (configure is not null)
            client.ConfigureRequest = configure;
        var socket = await client.ConnectAsync(new Uri(baseAddress, "/spark/live"), CancellationToken.None);
        return new LiveTestClient(socket);
    }

    public Task SubscribeQueryAsync(string sub, object query) => SendAsync(new { op = "subscribe", sub, query });
    public Task SubscribePoAsync(string sub, string objectTypeId, string id) => SendAsync(new { op = "subscribe", sub, po = new { objectTypeId, id } });
    public Task UpdateAsync(string sub, object query) => SendAsync(new { op = "update", sub, query });
    public Task UnsubscribeAsync(string sub) => SendAsync(new { op = "unsubscribe", sub });

    public Task SendAsync(object operation)
        => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(operation), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next frame that is not a ping, within <paramref name="timeout"/> (default 10 s); throws on timeout or close.</summary>
    public async Task<JsonElement> NextAsync(TimeSpan? timeout = null)
        => await TryNextAsync(timeout ?? TimeSpan.FromSeconds(10))
           ?? throw new TimeoutException($"No live frame within {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds} s (close status: {socket.CloseStatus})");

    /// <summary>The next frame that is not a ping, or null if none arrived within <paramref name="timeout"/> or the socket closed.</summary>
    public async Task<JsonElement?> TryNextAsync(TimeSpan timeout)
    {
        if (pending.Count > 0)
        {
            var first = pending[0];
            pending.RemoveAt(0);
            return first;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
                return null;
            receiving ??= socket.ReceiveAsync(buffer, CancellationToken.None);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || await Task.WhenAny(receiving, Task.Delay(remaining)) != receiving)
                return null;

            var received = await receiving;
            receiving = null;
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
                continue;

            var bytes = message.ToArray();
            message.SetLength(0);
            FramesReceived++;
            BytesReceived += bytes.Length;
            var frame = JsonDocument.Parse(bytes).RootElement.Clone();
            if (frame.GetProperty("type").GetString() == "ping")
                continue;
            return frame;
        }
    }

    /// <summary>Waits for a frame of <paramref name="type"/> for <paramref name="sub"/>, skipping others.</summary>
    public async Task<JsonElement> NextAsync(string type, string sub, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            var frame = remaining > TimeSpan.Zero ? await TryNextAsync(remaining) : null;
            if (frame is null)
                throw new TimeoutException($"No '{type}' frame for '{sub}' in time (close status: {socket.CloseStatus})");
            if (frame.Value.GetProperty("type").GetString() == type && frame.Value.TryGetProperty("sub", out var s) && s.GetString() == sub)
                return frame.Value;
        }
    }

    /// <summary>Reads until the server closes the socket; returns its close status (null on timeout).</summary>
    public async Task<WebSocketCloseStatus?> ClosedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            if (await TryNextAsync(deadline - DateTime.UtcNow) is null && socket.State is WebSocketState.Open)
                break;
        }
        return socket.CloseStatus;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch
        {
        }
        socket.Dispose();
    }
}
