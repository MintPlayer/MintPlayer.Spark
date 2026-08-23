using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using WebhooksDemo;

var builder = WebApplication.CreateBuilder(args);

var envPrefix = builder.Environment.EnvironmentName;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddWebhooksDemo();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    // Mounted through Spark rather than with endpoints.MapControllers(), so the controllers
    // share Spark's pipeline — its authentication schemes, its antiforgery scope, and
    // [SparkAuthorize]. A bare MapControllers() is reported by SPARK010.
    spark.AddControllers();
    spark.UseControllers();

    // #300, demonstrated: /api/github/projects/{id}/sync-columns is a cookie-authenticated POST
    // that had no CSRF check, because nothing attaches IAntiforgeryMetadata to a controller and
    // MVC's own [ValidateAntiForgeryToken] implements a different interface entirely. Naming /api
    // here covers it with no per-endpoint annotation. The SPA already echoes the XSRF-TOKEN cookie
    // — withSparkAuth() wires withXsrfConfiguration — so nothing else changes.
    spark.AddAntiforgeryProtection(antiforgery =>
    {
        antiforgery.PathPrefixes = ["/spark", "/connect", "/api"];
        antiforgery.RequireAntiforgery = true;
    });

    spark.UseContext<WebhooksDemoSparkContext>();
    spark.AddActions();
    // GitHub is the only way in, so the local email/password surface is not mapped at all —
    // register, login, refresh, confirmEmail, resendConfirmationEmail, forgotPassword,
    // resetPassword and POST manage/info are absent from the route table.
    spark.AddAuthentication<SparkUser>(
        configure: auth => auth.LocalCredentials = SparkLocalCredentials.Disabled,
        configureProviders: identity =>
    {
        identity.AddGitHub(options =>
        {
            options.ClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"] ?? string.Empty;
            options.ClientSecret = builder.Configuration[$"GitHub:{envPrefix}:ClientSecret"] ?? string.Empty;
            options.SaveTokens = true;
        });
    });
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
        options.ClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"];
        options.PrivateKeyPath = builder.Configuration[$"GitHub:{envPrefix}:PrivateKeyPath"];

        if (long.TryParse(builder.Configuration["GitHub:Production:AppId"], out var prodId))
            options.ProductionAppId = prodId;

        if (long.TryParse(builder.Configuration["GitHub:Development:AppId"], out var devId))
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

// Model synchronization is a build step, not a run mode: it writes App_Data/Model/*.json from the
// entity classes and needs no database, so it runs here and the process returns before Build().
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

// Writes a starting App_Data/security.json for an application that has none. Never overwrites.
if (builder.InitializeSparkSecurityIfRequested(args))
    return;

// --spark-verify-security fails the build when the set of rights reachable WITHOUT signing in
// has changed. security.json is a data file: widening it is a one-line diff that reads no
// differently from narrowing it, so the baseline is what makes the change reviewable.
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (_, next) => await next());
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
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