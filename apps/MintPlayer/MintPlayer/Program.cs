using System.Text.RegularExpressions;
using MintPlayer;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Services;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Boards;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.SoftDelete;

var builder = WebApplication.CreateBuilder(args);

// mintplayer.com. Production: Data Protection and forwarded headers come from Spark:DataProtection and
// Spark:ForwardedHeaders in the deployment's environment, never from appsettings.json.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MintPlayerContext>();
    spark.AddActions();
    spark.AddMigrations();

    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
        auth.Passkeys = SparkPasskeys.Enabled;
        // localhost in Development, mintplayer.com in production (a passkey is bound to its domain).
        auth.PasskeyServerDomain = builder.Configuration["MintPlayer:PasskeyServerDomain"];
        auth.AuthenticatorIssuer = "MintPlayer";
        // The legacy site let a user skip their code for external sign-ins; the migrated flag maps here.
        auth.ExternalLoginTwoFactor.AllowUserBypass = true;
    }, identity =>
    {
        // As on the legacy site (MintPlayer.Web Startup.cs): any character. 80 of the 776 migrated user names have
        // spaces, parentheses or non-ASCII letters, and Identity validates the name on every update, so the default
        // set refused even a password reset for them. Spark's own validator still refuses '@'.
        identity.User.AllowedUserNameCharacters = string.Empty;
    });
    // Facebook, GitHub, Google, LinkedIn, Microsoft and Twitter from Spark:Auth:Providers; the secrets are in the
    // deployment's .env.
    spark.AddExternalProviders(builder.Configuration);

    spark.AddMessaging();
    spark.AddMailManager();

    spark.AddSoftDelete();
    spark.AddHistory();
    // #530: the home page is the "home" board (App_Data/boards.json); a signed-in user may rearrange it.
    spark.AddBoards();
    // docs/spark_ssr_PRD.md: every page is server-rendered with its data. Dates on a first visit render in
    // Brussels time (D10). /home is the "home" board, which names its path in boards.json (D15).
    spark.AddPrerendering(prerendering => prerendering.DefaultTimeZone = "Europe/Brussels");
});

builder.Services.AddScoped<MintPlayerAccess>();
// Liveness for the compose healthcheck and the deploy: no checks registered, so it depends on nothing that can be
// misconfigured, and a bad secret cannot restart-loop the container.
builder.Services.AddHealthChecks();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.MintPlayer";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

if (builder.InitializeSparkSecurityIfRequested(args))
    return;

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
    endpoints.MapMintPlayerEndpoints();
    endpoints.MapHealthChecks("/health").AllowAnonymous();
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";
            spa.UseSparkPrerendering();

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
