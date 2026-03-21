using Microsoft.Extensions.Primitives;
using System.Net.WebSockets;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

internal class DevWebSocketService : IDevWebSocketService
{
    private readonly List<SocketClient> _clients = [];

    public async Task NewSocketClient(SocketClient client)
    {
        _clients.Add(client);

        while (true)
        {
            await Task.Delay(1000);
            if (client.WebSocket.State is WebSocketState.CloseReceived or WebSocketState.Closed)
                break;
        }

        _clients.Remove(client);
    }

    public async Task SendToClients(IDictionary<string, StringValues> headers, string body)
    {
        var payload = $"{string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"))}\n\n{body}";

        foreach (var client in _clients.ToList())
        {
            if (client.WebSocket.State == WebSocketState.Open)
            {
                await client.SendMessage(payload);
            }
        }
    }
}
