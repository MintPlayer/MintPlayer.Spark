using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace MintPlayer.Spark.Webhooks.GitHub.Extensions;

public static class SparkBuilderExtensions
{
    public static ISparkBuilder AddGithubWebhooks(
        this ISparkBuilder builder,
        Action<GitHubWebhooksOptions> configure)
    {
        var options = new GitHubWebhooksOptions();
        configure(options);

        builder.Services.Configure<GitHubWebhooksOptions>(opt =>
        {
            opt.WebhookSecret = options.WebhookSecret;
            opt.WebhookPath = options.WebhookPath;
            opt.ProductionAppId = options.ProductionAppId;
            opt.DevelopmentAppId = options.DevelopmentAppId;
            opt.DevWebSocketPath = options.DevWebSocketPath;
            opt.AllowedDevUsers = options.AllowedDevUsers;
            opt.DevSocketFilter = options.DevSocketFilter;
            opt.ClientId = options.ClientId;
            opt.PrivateKeyPem = options.PrivateKeyPem;
            opt.PrivateKeyPath = options.PrivateKeyPath;
        });

        // Register services (source-generated from [Register] attributes)
        builder.Services.AddSparkWebhooksGitHubServices();

        // The dev WebSocket's client list, registered whatever DevelopmentAppId says here. Whether the
        // dev tunnel is on is decided from IOptions<GitHubWebhooksOptions>, by the endpoint's IsEnabled
        // and by the webhook processor, and an app may set DevelopmentAppId through Configure<>()
        // after this call; a registration keyed on the local copy then left the mapped endpoint
        // unable to activate. Registering it costs nothing: it is a plain singleton that starts no
        // work, built only when the endpoint or the processor first asks for it.
        builder.Services.AddSingleton<IDevWebSocketService, DevWebSocketService>();

        // Apply deferred registrations from dev-tunnel extension methods
        options.ApplyRegistrations(builder.Services);

        // GitHub is a source of MintPlayer.Spark.Webhooks: the generic receiver reads the exact body,
        // refuses a bad X-Hub-Signature-256 with 401 at the request, and hands the delivery to the
        // processor inline. Its route is its own (WebhookPath), not /spark/webhooks/github.
        builder.AddWebhookSource(GitHubWebhookSource.Name, GitHubWebhookSource.Configure);

        // Generator endpoints, both reading IOptions<GitHubWebhooksOptions> at map time: the webhook
        // POST (GitHubWebhookEndpoint, at WebhookPath) and the dev WebSocket (DevWebSocketEndpoint),
        // which maps itself only when DevelopmentAppId is set and at the configured DevWebSocketPath.
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkWebhooksGitHubEndpoints());

        return builder;
    }
}
