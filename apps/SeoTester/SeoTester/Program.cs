using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Sharing;
using MintPlayer.Spark.Http;
using SeoTester;
using SeoTester.Audits;
using SeoTester.Catalogue;
using SeoTester.Entities;
using SeoTester.Settings;
using SeoTester.Verification;

var builder = WebApplication.CreateBuilder(args);

// SeoTester (#268) is a production app at seotester.mintplayer.com: treat changes here as production changes.
// The composition is docs/seo_analyzer_PRD.md §4.1. Data Protection, forwarded headers, expiration (X1) and live
// updates (X4) are Spark core and need no call: they read Spark:DataProtection, Spark:ForwardedHeaders and
// Spark:Live, and expiration switches itself on for the IExpiring types the packages below declare.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<SeoContext>();
    // Generated over this assembly: the Actions classes (Actions/), the custom actions (CustomActions/), the
    // interceptors that keep a site's server-owned fields (Interceptors/) and the re-verification sweep (cron).
    // AddRecipients() arrives with the pipeline's recipients (M9).
    spark.AddActions();
    spark.AddCustomActions();
    spark.AddInterceptors();
    spark.AddCronJobs();
    // Runs the migrations the referenced packages ship (Authorization's SparkUser/SparkRole, …) once at startup.
    spark.AddMigrations();

    // External sign-in only (D2): no passwords, no registration form. A user signs in with Microsoft, GitHub, X or
    // Google; a second provider with the same verified address is linked only after a mailed confirmation (D7).
    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Disabled;
        auth.ExternalLoginLinking = SparkExternalLoginLinking.ConfirmByEmail;

        // Passkeys (owner decision 2026-10-10, PRD D46): a user adds one on the account page after the first external
        // sign-in, a second way in that does not depend on one provider. Enrolment needs a session, so this opens no
        // account that a provider did not open first. LocalCredentials stays Disabled: a passkey is no password.
        auth.Passkeys = SparkPasskeys.Enabled;
        // ⚠️ The relying-party id is pinned, never taken from the request: a passkey binds to it for life. Production
        // reads it from Spark:Auth:PublicBaseUrl (seotester.mintplayer.com), Development uses localhost.
        if (Uri.TryCreate(builder.Configuration["Spark:Auth:PublicBaseUrl"], UriKind.Absolute, out var publicBaseUrl))
            auth.PasskeyServerDomain = publicBaseUrl.Host;
        else if (builder.Environment.IsDevelopment())
            auth.PasskeyServerDomain = "localhost";
    });
    // Spark:Auth:Providers:{MicrosoftAccount,GitHub,Twitter,Google}: a provider without a ClientId is not registered,
    // so the credentials live only in user secrets / the server's environment (docs/seotester/README.md).
    // Development also registers SparkId (the demo identity provider) so the app signs in without any of them.
    // With no provider at all, Spark refuses to start (LocalCredentials = Disabled): nobody could sign in.
    spark.AddExternalProviders(builder.Configuration);

    // Account mail, link confirmations (ConfirmByEmail needs a sender), invitations and notification mail. X and
    // Entra work accounts give no verified address, so mail goes only to confirmed ones. Transport from
    // Spark:Mail (SMTP in production, the pickup folder in Development).
    spark.AddMailManager();

    // The crawl pipeline's queues (SeoTester.Messaging.SeoQueues) are declared in Spark:Messaging:Queues; their
    // recipients arrive with M9/M10. Web Push deliveries ride the same outbox.
    spark.AddMessaging();
    spark.AddHistory();

    // X2: every fetch of a user-chosen URL goes through the SSRF-safe client (Spark:SafeHttp).
    spark.AddSafeHttp();

    // X9 + X3: every gate and limit of PRD §6 and the category weights, administrators only
    // (security.json binds settings:administrators to Administrators); the per-user run limits are quotas.
    spark.AddSettings(typeof(SeoSettings));
    spark.AddQuotas(typeof(SeoQuotas));

    // X10: in-app, mail and Web Push (Spark:Notifications:WebPush, VAPID keys from secrets; without them push is off).
    spark.AddNotifications();
    // X11: personal organization on first sign-in, invitations, join requests, the membership row filter.
    spark.AddOrganizations();
    // X6: share links (D31): what a link shows is an allow-list; SharingSwitch is the administrator's switch (D11).
    spark.AddShareLinks(o =>
    {
        o.Share<Site>(
            attributes: ["Origin", "IsVerified", "LatestScore", "LatestGrade", "LatestRunAtUtc"],
            queries: new Dictionary<string, string[]> { ["site-runs"] = ["StartedAtUtc", "Status", "Score", "Grade", "ScoringVersion"] });
        o.Share<AuditRun>(
            attributes: ["Origin", "Status", "StartedAtUtc", "CompletedAtUtc", "Score", "Grade", "ScoringVersion", "CategoryScores", "CheckResults"],
            queries: new Dictionary<string, string[]>());
    });
});

builder.Services.AddScoped<ISparkShareGate, SharingSwitch>();

// M8: verification (F3), the audit start (§4.3), the catalogue (D32), the abuse log (D28) and D26's cascade.
builder.Services.AddSingleton<IDnsTxtResolver, DnsClientTxtResolver>();
builder.Services.AddSafeHttpClient(SiteVerification.HttpClientName, builder.Configuration.GetSection("Spark:SafeHttp"), o =>
{
    // A proof must come from the site itself: an open redirect elsewhere proves nothing.
    o.RedirectsWithinOriginOnly = true;
    o.MaxRedirects = 3;
    o.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<SiteVerification>();
builder.Services.AddScoped<AuditStarter>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<CatalogueSnapshots>();
builder.Services.AddScoped<IOrganizationDataHandler, SeoOrganizationData>();
builder.Services.AddHostedService<CatalogueSeeder>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.SeoTester";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// The --spark-* commands are build steps, not run modes: they need no database or credentials and return before Build().
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;
if (builder.InitializeSparkSecurityIfRequested(args))
    return;
// --spark-verify-security fails the build when the rights reachable WITHOUT signing in change (App_Data/securityPosture.txt).
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    // The app's own generator endpoints: GET /bot (Bot/BotPageEndpoint.cs).
    endpoints.MapSeoTesterEndpoints();
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
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

public partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}
