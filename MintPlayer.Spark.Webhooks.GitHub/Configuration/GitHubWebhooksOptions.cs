using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Webhooks.GitHub.Configuration;

public class GitHubWebhooksOptions
{
    private readonly List<Action<IServiceCollection>> _serviceActions = [];

    /// <summary>Webhook secret configured in the GitHub App settings.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Webhook endpoint path. Defaults to "/api/github/webhooks".</summary>
    public string WebhookPath { get; set; } = "/api/github/webhooks";

    /// <summary>GitHub App ID for the production app.</summary>
    public long? ProductionAppId { get; set; }

    /// <summary>
    /// GitHub App ID for the development app. When set, webhooks from this app
    /// are forwarded to connected dev clients instead of being processed locally.
    /// </summary>
    public long? DevelopmentAppId { get; set; }

    /// <summary>
    /// WebSocket path for dev forwarding endpoint. Defaults to "/spark/github/dev-ws".
    /// Only active when DevelopmentAppId is set.
    /// </summary>
    public string DevWebSocketPath { get; set; } = "/spark/github/dev-ws";

    /// <summary>
    /// Allowed GitHub usernames for WebSocket dev connections.
    /// If empty, all authenticated connections are accepted.
    /// </summary>
    public List<string> AllowedDevUsers { get; set; } = [];

    /// <summary>
    /// Used by dev-tunnel extension methods to register background services.
    /// </summary>
    public void RegisterService(Action<IServiceCollection> registration)
        => _serviceActions.Add(registration);

    internal void ApplyRegistrations(IServiceCollection services)
    {
        foreach (var action in _serviceActions)
            action(services);
    }
}
