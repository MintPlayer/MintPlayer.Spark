using System.Text.RegularExpressions;
using Iconic;
using Iconic.Endpoints;
using Iconic.Services;
using Microsoft.AspNetCore.ResponseCompression;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;

var builder = WebApplication.CreateBuilder(args);

// iconic.mintplayer.com (docs/iconic_PRD.md). Production: Data Protection and forwarded headers come from
// Spark:DataProtection and Spark:ForwardedHeaders in the deployment's environment, never from appsettings.json.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseRavenDb();
    spark.UseContext<IconicContext>();
    spark.AddActions();

    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
        auth.Passkeys = SparkPasskeys.Enabled;
        auth.PasskeyServerDomain = builder.Configuration["Iconic:PasskeyServerDomain"];
        auth.AuthenticatorIssuer = "Iconic";
    });
    // Google, Microsoft, GitHub, … from Spark:Auth:Providers (D7); the secrets are in user secrets or the .env.
    spark.AddExternalProviders(builder.Configuration);

    // Spark's per-IP budget, also over the editor's API. Assigning replaces the defaults, so they are listed.
    spark.AddRateLimiter(rateLimiter => rateLimiter.PathPrefixes = ["/spark", "/connect", "/api/iconic"]);

    spark.AddMessaging();
    spark.AddMailManager();
    spark.AddHistory();
});

builder.Services.AddScoped<IconicAccess>();
builder.Services.AddScoped<ProjectService>();
builder.Services.Configure<IconicQuotas>(builder.Configuration.GetSection("Iconic:Quotas"));
builder.Services.AddIconicRateLimits(builder.Configuration);
builder.Services.AddHealthChecks();

builder.Services.ConfigureApplicationCookie(options => options.Cookie.Name = ".SparkAuth.Iconic");

builder.Services.AddSpaStaticFilesImproved(configuration => configuration.RootPath = "ClientApp/dist/ClientApp/browser");

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml", "application/manifest+json"]);
});
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

if (builder.InitializeSparkSecurityIfRequested(args))
    return;

if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        // A shared drawing's page is unlisted (D20): search engines must not index it.
        if (context.Request.Path.StartsWithSegments("/s") || context.Request.Path.StartsWithSegments("/api/iconic/shared"))
            headers["X-Robots-Tag"] = "noindex, nofollow";
        return Task.CompletedTask;
    });
    return next(context);
});
app.UseResponseCompression();

if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (hashedAsset().IsMatch(context.File.Name))
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    },
});

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    endpoints.MapIconicEndpoints();
    endpoints.MapHealthChecks("/health").AllowAnonymous();
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark") && !context.Request.Path.StartsWithSegments("/api"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";
            if (app.Environment.IsDevelopment())
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [openBrowserRegex()]);
        });
    });

app.Run();

public partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();

    [GeneratedRegex(@"-[A-Z0-9]{8}\.(js|css|woff2?|ttf|svg|png|jpg|webp)$")]
    private static partial Regex hashedAsset();
}
