using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using SparkId;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<SparkIdContext>();
    spark.AddMigrations(); // generated: discovers ISparkMigration classes, runs them once at startup

    // SparkId is the demo identity provider (docs/identity_provider_platform_PRD.md R1): HR, Fleet and
    // QnA sign in here. Its own users sign in with a password or a passkey, so both are on.
    spark.AddAuthentication<SparkUser>(
        configure: auth =>
        {
            auth.LocalCredentials = SparkLocalCredentials.Full;
            auth.Passkeys = SparkPasskeys.Enabled;
        });

    // Issuer is pinned rather than derived from the Host header: outside Development the provider
    // requires it, because a caller-controlled issuer is a caller-controlled token audience.
    spark.AddIdentityProvider(options =>
    {
        options.Issuer = builder.Configuration["SparkIdentityProvider:Issuer"]
            ?? "https://localhost:5011"; // the https launch profile
        options.SigningKeyPath = builder.Configuration["SparkIdentityProvider:SigningKeyPath"]
            ?? options.SigningKeyPath;
    });

    spark.AddMessaging();
    // Registration, developer requests and app invitations send mail. Demo app: every mail is
    // written as an .eml file into Spark:Mail:PickupFolder (appsettings.json) instead of being sent.
    spark.AddMailManager();
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.SparkId";
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
// has changed.
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
});

// /connect/* and /.well-known/* are server-rendered or JSON (PRD D7); only the rest is the SPA.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/connect")
        && !context.Request.Path.StartsWithSegments("/.well-known"),
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
