using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

internal class DevWebSocketService : IDevWebSocketService
{
    // R2-M8: bare List<T> was mutated from concurrent Add/Remove (one task per
    // inbound WS) and iterated from SendToClients — racy. Switched to
    // ConcurrentDictionary keyed by reference so add/remove are lock-free and
    // iteration produces a stable snapshot.
    private readonly ConcurrentDictionary<SocketClient, byte> _clients = new();

    public async Task NewSocketClient(SocketClient client)
    {
        _clients[client] = 0;
        try
        {
            while (true)
            {
                await Task.Delay(1000);
                if (client.WebSocket.State is WebSocketState.CloseReceived or WebSocketState.Closed)
                    break;
            }
        }
        finally
        {
            _clients.TryRemove(client, out _);
        }
    }

    public async Task SendToClients(IDictionary<string, StringValues> headers, string body)
    {
        var payload = $"{string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"))}\n\n{body}";

        foreach (var client in _clients.Keys)
        {
            if (client.WebSocket.State == WebSocketState.Open)
            {
                await client.SendMessage(payload);
            }
        }
    }
}
