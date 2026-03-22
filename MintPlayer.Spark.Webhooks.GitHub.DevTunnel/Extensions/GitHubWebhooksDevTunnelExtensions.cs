using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;

public static class GitHubWebhooksDevTunnelExtensions
{
    /// <summary>
    /// Adds a smee.io tunnel for receiving webhooks during local development.
    /// The developer configures the smee channel URL as the Webhook URL in the GitHub App settings.
    /// </summary>
    public static GitHubWebhooksOptions AddSmeeDevTunnel(
        this GitHubWebhooksOptions options,
        string smeeChannelUrl)
    {
        options.RegisterService(services =>
        {
            services.Configure<SmeeOptions>(opt =>
            {
                opt.ChannelUrl = smeeChannelUrl;
            });

            services.AddHostedService<SmeeBackgroundService>();
        });

        return options;
    }

    /// <summary>
    /// Connects to a production server's WebSocket endpoint to receive forwarded dev webhooks.
    /// The production server must have DevelopmentAppId configured.
    /// Uses the GitHub token to authenticate and determine the developer's GitHub username.
    /// </summary>
    public static GitHubWebhooksOptions AddWebSocketDevTunnel(
        this GitHubWebhooksOptions options,
        string productionWebSocketUrl,
        string githubToken)
    {
        options.RegisterService(services =>
        {
            services.Configure<WebSocketDevTunnelOptions>(opt =>
            {
                opt.ProductionWebSocketUrl = productionWebSocketUrl;
                opt.GitHubToken = githubToken;
            });

            services.AddHostedService<WebSocketDevClientService>();
        });

        return options;
    }
}
