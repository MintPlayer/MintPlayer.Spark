using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;

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

        // Register endpoint mapping via SparkModuleRegistry
        builder.Registry.AddEndpoints(endpoints =>
        {
            // Map the Octokit webhook endpoint with signature validation
            // DisableAntiforgery: GitHub POSTs webhooks without XSRF tokens
            // It stays hand-mapped: Octokit owns the request-level X-Hub-Signature-256 refusal
            // (docs/endpoints_generator_webhooks_exception.md).
            endpoints.MapGitHubWebhooks(options.WebhookPath, options.WebhookSecret)
                .DisableAntiforgery();

            // Generator endpoints: the dev WebSocket (DevWebSocketEndpoint), which maps itself only
            // when DevelopmentAppId is set and at the configured DevWebSocketPath.
            endpoints.MapSparkWebhooksGitHubEndpoints();
        });

        return builder;
    }
}
