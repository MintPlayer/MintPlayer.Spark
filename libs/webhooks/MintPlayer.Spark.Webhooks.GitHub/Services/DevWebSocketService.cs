using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
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
    private readonly IOptions<GitHubWebhooksOptions> _options;

    public DevWebSocketService(IOptions<GitHubWebhooksOptions> options)
    {
        _options = options;
    }

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

    public async Task SendToClients(IDictionary<string, StringValues> headers, string body, GitHubWebhookRoutingContext context)
    {
        var payload = $"{string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"))}\n\n{body}";
        var filter = _options.Value.DevSocketFilter;

        foreach (var client in _clients.Keys)
        {
            if (client.WebSocket.State != WebSocketState.Open)
                continue;

            // Without this, every connected developer receives every other developer's webhook
            // traffic. AllowedDevUsers gates who may connect, not who receives which delivery.
            if (!filter(context, client.GitHubUsername))
                continue;

            await client.SendMessage(payload);
        }
    }
}
