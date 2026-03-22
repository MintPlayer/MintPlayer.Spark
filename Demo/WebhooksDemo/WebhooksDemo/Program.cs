using MintPlayer.Spark;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using WebhooksDemo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<WebhooksDemoSparkContext>();
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;

        if (long.TryParse(builder.Configuration["GitHub:ProductionAppId"], out var prodId))
            options.ProductionAppId = prodId;

        if (long.TryParse(builder.Configuration["GitHub:DevelopmentAppId"], out var devId))
            options.DevelopmentAppId = devId;

        // Uncomment ONE of these for local development:
        // Option A: smee.io tunnel (no production deployment needed)
        // options.AddSmeeDevTunnel(builder.Configuration["GitHub:SmeeChannelUrl"]!);

        // Option B: WebSocket from production (production deployment exists)
        // options.AddWebSocketDevTunnel(
        //     builder.Configuration["GitHub:DevWebSocketUrl"]!,
        //     builder.Configuration["GitHub:DevGitHubToken"]!);
    });
});

var app = builder.Build();

app.UseRouting();
app.UseSpark();

app.MapSpark();

app.Run();
