using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;
using System.Net.WebSockets;

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
        });

        // Register the webhook event processor (scoped — one per request)
        builder.Services.AddScoped<WebhookEventProcessor, SparkWebhookEventProcessor>();

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
            // Map the Octokit webhook endpoint (signature validation is handled in our processor)
            endpoints.MapGitHubWebhooks(options.WebhookPath);

            // Map the dev WebSocket endpoint if DevelopmentAppId is configured
            if (options.DevelopmentAppId.HasValue)
            {
                MapDevWebSocketEndpoint(endpoints, options);
            }
        });

        return builder;
    }

    private static void MapDevWebSocketEndpoint(IEndpointRouteBuilder endpoints, GitHubWebhooksOptions options)
    {
        endpoints.Map(options.DevWebSocketPath, async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                SubProtocol = "wss",
                KeepAliveInterval = TimeSpan.FromMinutes(5),
            });

            try
            {
                // Receive handshake with GitHub token
                var handshake = await ws.ReadObject<Handshake>();
                if (handshake?.GithubToken == null)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Missing credentials", CancellationToken.None);
                    return;
                }

                // Validate GitHub token and resolve username
                var githubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("SparkWebhooks"));
                githubClient.Credentials = new Octokit.Credentials(handshake.GithubToken);
                var githubUser = await githubClient.User.Current();

                // Check against allowed users list (if configured)
                var opts = context.RequestServices.GetRequiredService<IOptions<GitHubWebhooksOptions>>().Value;
                if (opts.AllowedDevUsers.Count > 0 && !opts.AllowedDevUsers.Contains(githubUser.Login))
                {
                    await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Unauthorized", CancellationToken.None);
                    return;
                }

                var socketService = context.RequestServices.GetRequiredService<IDevWebSocketService>();
                await socketService.NewSocketClient(new SocketClient(ws, githubUser.Login));
            }
            catch (Octokit.AuthorizationException)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
        });
    }
}
