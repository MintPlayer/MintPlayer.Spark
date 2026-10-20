using System.Text.RegularExpressions;
using HR;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.ResourceServer;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration, spark =>
{
    // Mounted through Spark rather than with endpoints.MapControllers(), so the controllers
    // share Spark's pipeline — its authentication schemes, its antiforgery scope, and
    // [SparkAuthorize]. A bare MapControllers() is reported by SPARK010.
    spark.AddControllers();
    spark.UseControllers();

    spark.UseContext<HRContext>();
    spark.AddSparkDiscovered(); // generated: the Actions classes and the ISparkMigration classes (run once at startup)

    // Explicit since preview.60: the default is now Disabled, matching the client's opt-in
    // routes. HR mounts the full password family, so it says so.
    // Passkeys alongside passwords — the other shape worth exercising. CodeCoverage runs them with
    // LocalCredentials Disabled, HR runs them with Full; the two options are independent and both
    // combinations have to work.
    spark.AddAuthentication<SparkUser>(
        configure: auth =>
        {
            auth.LocalCredentials = SparkLocalCredentials.Full;
            auth.Passkeys = SparkPasskeys.Enabled;
        });

    // External providers from Spark:Auth:Providers. appsettings.Development.json registers SparkId
    // (the demo identity provider, https://localhost:5011) as the OpenID Connect scheme "SparkId";
    // SparkId seeds the matching "hr" client in Development. HR hosted the provider itself until
    // SparkId existed (docs/identity_provider_platform_PRD.md R1).
    // The resource-server demo (docs/identity_provider_platform_PRD.md I12): HR also asks SparkId for
    // fleet.read (optional, so the consent page lets the user untick it) and keeps the tokens, which
    // the external-login callback stores on the user, so /api/hr/fleet-cars can call Fleet's API.
    spark.AddExternalProviders(builder.Configuration, hooks => hooks.OpenIdConnect("SparkId", oidc =>
    {
        oidc.Scope.Add("fleet.read");
        oidc.SaveTokens = true;
    }));

    // Agents sign in at SparkId too (setup 2, docs/guide-mcp.md "A Spark identity provider as the external server"):
    // SparkId lists HR's /mcp as a resource it signs agents in for (idp.ForMcpResource, its appsettings.Development.json),
    // and HR accepts SparkId's tokens for its canonical URL (Spark:Mcp:CanonicalUrl), mapped to the HR user who linked
    // that SparkId account (the "SparkId" sign-in above), never by email. Only where the SparkId sign-in is configured.
    if (builder.Configuration["Spark:Auth:Providers:OpenIdConnect:SparkId:Authority"] is { Length: > 0 } sparkId)
        spark.AddExternalAgentAuthorizationServer(sparkId, loginProvider: "SparkId");

    // MCP server (docs/guide-mcp.md, PRD D24): agents read and, through propose_changes → apply_changes, change
    // people and their jobs (the contracts: CarreerJob rows, "agentPermissions": "Read, Write" in the model, with
    // Person, which owns them). Professions and companies are readable so a job's references stay visible.
    // Setups: personal access tokens (account page, Connected applications) everywhere, and SparkId's agent tokens
    // where the SparkId sign-in is configured (above).
    spark.AddMcp(mcp =>
    {
        mcp.ServerName = "HR";
        mcp.ToolPrefix = "hr";
        mcp.Instructions = "People, their companies, professions and jobs (contracts) in the HR demo, as the signed-in user sees them. Changes go through propose_changes, then apply_changes.";
        mcp.AgentPermissions = agent => agent.Kind == EAgentKind.Anonymous
            ? EAgentPermission.Read
            : EAgentPermission.Read | EAgentPermission.Write;
    });

    spark.AddMessaging();
    // #460 D6: registration needs somewhere to send account mail. Demo app: every mail is written
    // as an .eml file into Spark:Mail:PickupFolder (appsettings.json) instead of being sent.
    spark.AddMailManager();

    // Everything comes from the `Spark:Replication` section, bound by AddReplication. HR reads Fleet's cars through
    // its [Replicated] class (HR.Replicated.Car); what HR owns (people, companies) other modules read through their own
    // classes, which HR approves under "Replication requests" (automatically while HR runs in Development).
    spark.AddReplication();
});

// For /api/hr/fleet-cars (the I12 demo).
builder.Services.AddHttpClient();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.HR";
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

if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    // The app's own generator endpoints (Api/FleetCarsProxyEndpoint.cs, the I12 resource-server demo).
    MintPlayer.AspNetCore.Endpoints.Generated.HREndpointsExtensions.MapHREndpoints(endpoints);
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

partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}