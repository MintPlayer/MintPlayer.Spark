using Microsoft.Extensions.Primitives;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

internal interface IDevWebSocketService
{
    Task NewSocketClient(SocketClient client);
    Task SendToClients(IDictionary<string, StringValues> headers, string body);
}
