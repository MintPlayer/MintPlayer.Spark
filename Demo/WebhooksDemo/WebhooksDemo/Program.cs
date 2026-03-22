using MintPlayer.Spark;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
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

        // Local development: smee.io tunnel
        var smeeUrl = builder.Configuration["GitHub:SmeeChannelUrl"];
        if (!string.IsNullOrEmpty(smeeUrl))
        {
            options.AddSmeeDevTunnel(smeeUrl);
        }

        // Alternative: WebSocket from production (uncomment if deployed)
        // var wsUrl = builder.Configuration["GitHub:DevWebSocketUrl"];
        // var wsToken = builder.Configuration["GitHub:DevGitHubToken"];
        // if (!string.IsNullOrEmpty(wsUrl) && !string.IsNullOrEmpty(wsToken))
        // {
        //     options.AddWebSocketDevTunnel(wsUrl, wsToken);
        // }
    });
});

var app = builder.Build();

app.UseRouting();
app.UseSpark();

app.MapSpark();
app.MapGet("/health", () => Results.Ok()).DisableAntiforgery();

app.Run();
