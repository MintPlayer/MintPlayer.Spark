using System.Net.WebSockets;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

internal class SocketClient
{
    public WebSocket WebSocket { get; }
    public string GitHubUsername { get; }

    public SocketClient(WebSocket webSocket, string gitHubUsername)
    {
        WebSocket = webSocket;
        GitHubUsername = gitHubUsername;
    }

    public Task SendMessage(string message)
        => WebSocket.WriteMessage(message);
}
