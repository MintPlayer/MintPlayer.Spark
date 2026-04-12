using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using WebhooksDemo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddWebhooksDemo();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<WebhooksDemoSparkContext>();
    spark.AddActions();
    spark.AddAuthorization(options => options.DefaultBehavior = MintPlayer.Spark.Authorization.Configuration.DefaultAccessBehavior.AllowAll);
    spark.AddAuthentication<SparkUser>(configureProviders: identity =>
    {
        identity.AddGitHub(options =>
        {
            options.ClientId = builder.Configuration["GitHub:ClientId"] ?? string.Empty;
            options.ClientSecret = builder.Configuration["GitHub:ClientSecret"] ?? string.Empty;
            options.Scope.Add("read:user");
            options.Scope.Add("read:org");
            options.Scope.Add("project");
            options.SaveTokens = true;
        });
    });
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
        options.ClientId = builder.Configuration["GitHub:ClientId"];
        options.PrivateKeyPath = builder.Configuration["GitHub:PrivateKeyPath"];

        if (long.TryParse(builder.Configuration["GitHub:ProductionAppId"], out var prodId))
            options.ProductionAppId = prodId;

        if (long.TryParse(builder.Configuration["GitHub:DevelopmentAppId"], out var devId))
            options.DevelopmentAppId = devId;

        // Local development: smee.io tunnel (when no production deployment exists)
        var smeeUrl = builder.Configuration["GitHub:SmeeChannelUrl"];
        if (!string.IsNullOrEmpty(smeeUrl))
        {
            options.AddSmeeDevTunnel(smeeUrl);
        }

        // WebSocket dev tunnel: receive forwarded webhooks from production
        var wsUrl = builder.Configuration["GitHub:DevWebSocketUrl"];
        var wsToken = builder.Configuration["GitHub:DevGitHubToken"];
        if (!string.IsNullOrEmpty(wsUrl) && !string.IsNullOrEmpty(wsToken))
        {
            options.AddWebSocketDevTunnel(wsUrl, wsToken);
        }
    });
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

app.Use(async (_, next) => await next());
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark(o => o.SynchronizeModelsIfRequested<WebhooksDemoSparkContext>(args));

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
    endpoints.MapGet("/health", () => Results.Ok());
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/api"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [openBrowserRegex()]);
            }
        });
    });

app.Run();

partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}