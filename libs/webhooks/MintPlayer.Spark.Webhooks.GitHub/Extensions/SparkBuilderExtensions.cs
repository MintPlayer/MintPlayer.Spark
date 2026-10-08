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

        // Register dev WebSocket forwarding service if DevelopmentAppId is configured
        if (options.DevelopmentAppId.HasValue)
        {
            builder.Services.AddSingleton<IDevWebSocketService, DevWebSocketService>();
        }

        // Apply deferred registrations from dev-tunnel extension methods
        options.ApplyRegistrations(builder.Services);

        // Register endpoint mapping via SparkModuleRegistry
        builder.Registry.AddEndpoints(endpoints =>
        {
            // Map the Octokit webhook endpoint with signature validation
            // DisableAntiforgery: GitHub POSTs webhooks without XSRF tokens
            // It stays hand-mapped: Octokit owns the X-Hub-Signature-256 check and the event
            // dispatch (docs/endpoints_generator_webhooks_exception.md).
            endpoints.MapGitHubWebhooks(options.WebhookPath, options.WebhookSecret)
                .DisableAntiforgery();

            // Generator endpoints: the dev WebSocket (DevWebSocketEndpoint), which maps itself only
            // when DevelopmentAppId is set and at the configured DevWebSocketPath.
            endpoints.MapSparkWebhooksGitHubEndpoints();
        });

        return builder;
    }
}
