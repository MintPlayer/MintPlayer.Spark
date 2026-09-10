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
    /// GitHub usernames allowed to connect to the dev-forwarding WebSocket.
    /// <para>
    /// <b>Empty means nobody</b> — the dev tunnel is off until you name someone. A webhook
    /// delivered to <c>DevelopmentAppId</c> carries private-repo data, so an unset allow-list
    /// must not mean "any authenticated GitHub user"; that let a throwaway account subscribe
    /// to all of it (R2-H12).
    /// </para>
    /// </summary>
    public List<string> AllowedDevUsers { get; set; } = [];

    /// <summary>
    /// Decides which connected developer receives a forwarded webhook. Given the delivery's
    /// routing context and a connected developer's GitHub login, return <see langword="true"/> to
    /// send them that delivery.
    /// <para>
    /// <b>Default: the delivery goes to the developer who caused it</b> — <c>sender.login</c>
    /// matched case-insensitively against the login the developer authenticated with. A payload
    /// with no sender falls back to every connected developer, so an unattributed delivery is
    /// never silently lost.
    /// </para>
    /// <para>
    /// <see cref="AllowedDevUsers"/> is not a substitute for this: it gates who may
    /// <em>connect</em>, not who receives <em>which</em> delivery. Without a filter, every
    /// connected developer sees every other developer's webhook traffic.
    /// </para>
    /// <para>
    /// To restore the previous fan-out-to-everyone behaviour:
    /// <code>options.DevSocketFilter = static (_, _) => true;</code>
    /// </para>
    /// </summary>
    public Func<GitHubWebhookRoutingContext, string, bool> DevSocketFilter { get; set; } =
        static (context, developerLogin) =>
            string.IsNullOrEmpty(context.SenderLogin)
            || string.Equals(context.SenderLogin, developerLogin, StringComparison.OrdinalIgnoreCase);

    /// <summary>GitHub App Client ID, used for JWT authentication when making API calls.</summary>
    public string? ClientId { get; set; }

    /// <summary>GitHub App private key PEM content. Either this or <see cref="PrivateKeyPath"/> is required for API calls.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Path to the GitHub App private key .pem file. Either this or <see cref="PrivateKeyPem"/> is required for API calls.</summary>
    public string? PrivateKeyPath { get; set; }

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
