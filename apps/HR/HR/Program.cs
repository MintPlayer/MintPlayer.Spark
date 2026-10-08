using System.Text.RegularExpressions;
using HR;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;
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
    spark.AddActions();
    spark.AddMigrations(); // generated: discovers ISparkMigration classes, runs them once at startup

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

    spark.AddMessaging();
    // #460 D6: registration needs somewhere to send account mail. Demo app: every mail is written
    // as an .eml file into Spark:Mail:PickupFolder (appsettings.json) instead of being sent.
    spark.AddMailManager();

    // Everything else comes from the `Spark:Replication` section, bound by AddReplication.
    // Assemblies are the one setting configuration cannot express.
    spark.AddReplication(opt => opt.AssembliesToScan = [typeof(HR.Replicated.Car).Assembly]);
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

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();

    // I12 demo: Fleet's cars, read with the signed-in user's SparkId access token (fleet.read).
    // 409 when the user signed in another way, or did not grant fleet.read, or the token expired:
    // signing in through SparkId again fetches a fresh one.
    endpoints.MapGet("/api/hr/fleet-cars", async (
        HttpContext http,
        Microsoft.AspNetCore.Identity.UserManager<SparkUser> users,
        IHttpClientFactory clients,
        IConfiguration configuration) =>
    {
        if (await users.GetUserAsync(http.User) is not { } user
            || await users.GetAuthenticationTokenAsync(user, "SparkId", "access_token") is not { Length: > 0 } accessToken)
            return Results.Problem("Sign in through Spark Identity first.", statusCode: StatusCodes.Status409Conflict);

        var fleet = configuration["Demo:FleetBaseUrl"] ?? "https://localhost:5003";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fleet.TrimEnd('/')}/api/fleet/cars");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await clients.CreateClient().SendAsync(request, http.RequestAborted);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return Results.Problem("Fleet refused the token: it lacks fleet.read or has expired. Sign in through Spark Identity again.", statusCode: StatusCodes.Status409Conflict);
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(http.RequestAborted), "application/json");
    }).RequireAuthorization();
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