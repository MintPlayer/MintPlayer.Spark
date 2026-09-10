using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

internal interface IDevWebSocketService
{
    Task NewSocketClient(SocketClient client);

    /// <summary>
    /// Forwards one delivery to the connected developers that
    /// <see cref="GitHubWebhooksOptions.DevSocketFilter"/> selects for it.
    /// </summary>
    Task SendToClients(IDictionary<string, StringValues> headers, string body, GitHubWebhookRoutingContext context);
}
