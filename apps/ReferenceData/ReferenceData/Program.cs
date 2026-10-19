using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Seed;
using MintPlayer.Spark.ReferenceData.Sources.Holidays;
using MintPlayer.Spark.ReferenceData.Sources.Tedb;
using ReferenceData;

var builder = WebApplication.CreateBuilder(args);

// apps/ReferenceData (docs/reference_data_PRD.md §4.16): a reference-data instance seeded with the basic set (RD9), and
// a consumer of itself. Local only (K8 revised): https 5017 / http 5018, database RefData on localhost:8080.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<ReferenceDataContext>();
    spark.AddSparkDiscovered();

    // Administrators sign in with a local password (RD-DEF 2); the first one comes from ReferenceData:Admin (user secrets).
    spark.AddAuthentication<SparkUser>(auth => auth.LocalCredentials = SparkLocalCredentials.Full);

    spark.AddMessaging();     // the refdata-import queue
    // Runs ASP.NET's rate limiter: /api/v1 carries its own named policy (a token bucket per IPv4 address or IPv6 /64);
    // Spark's own budget keeps metering /spark. /api stays out of PathPrefixes (they replace the defaults).
    spark.AddRateLimiter();

    // The instance: model, ledger, imports, releases, /api/v1, the seed. One line per adapter package (K7). Every
    // adapter is registered, so "Import now" can import any country locally (a source imports only when
    // Spark:ReferenceData:Sources:{id}:Enabled); the seed stays the basic set of Spark:ReferenceData:Seed:Countries (D39).
    spark.AddReferenceData()
        .AddGlobalBaseline()
        .AddBelgium()
        .AddNetherlands()
        .AddFrance()
        .AddLuxembourg()
        .AddSwitzerland()
        .AddLiechtenstein()
        .AddAustria()
        .AddDenmark()
        .AddNorway()
        .AddFinland()
        .AddEstonia()
        .AddLatvia()
        .AddSpain()
        .AddPoland()
        .AddCzechia()
        .AddSlovenia()
        .AddUnitedStates()
        .AddAustralia()
        .AddTedbVatRates()
        .AddStatutoryHolidays();

    // The consumer half (the address form, zone names, the sync), pointed at this instance in Development
    // (Spark:ReferenceData:Client:BaseAddress). Unset = free-text mode.
    spark.AddReferenceDataClient();
});

builder.Services.AddHealthChecks();
builder.Services.AddHostedService<AdminSeeder>();

builder.Services.ConfigureApplicationCookie(options => options.Cookie.Name = ".SparkAuth.ReferenceData");

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// The --spark-* commands are build steps: they need no database and return before Build().
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;
if (builder.InitializeSparkSecurityIfRequested(args))
    return;
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

// --refdata-build-seed / --refdata-verify-seed: the basic seed set from the adapters' fixtures. No database.
if (app.RunReferenceDataSeedCommandIfRequested(args))
    return;

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    endpoints.MapHealthChecks("/health").AllowAnonymous();
});

// /spark, /api (the public reference-data API) and /health are never the SPA's index.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/health"),
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
