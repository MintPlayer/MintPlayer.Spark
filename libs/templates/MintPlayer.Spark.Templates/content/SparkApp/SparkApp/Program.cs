#if (HasClient)
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
#endif
using MintPlayer.Spark;
using MintPlayer.Spark.Extensions;
#if (HasAuth)
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
#endif
#if (UseControllers)
using MintPlayer.Spark.Controllers;
#endif
#if (UseHistory)
using MintPlayer.Spark.History;
#endif
#if (UseLogging)
using MintPlayer.Spark.Logging;
#endif
#if (UseMail)
using MintPlayer.Spark.MailManager;
#endif
#if (UseMcp)
using MintPlayer.Spark.Mcp;
#endif
#if (UseMessaging)
using MintPlayer.Spark.Messaging;
#endif
#if (UseSoftDelete)
using MintPlayer.Spark.SoftDelete;
#endif
using SparkApp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration, spark =>
{
#if (IsRaven)
    // The store: RavenDB, configured from Spark:RavenDb in appsettings.json.
    spark.UseRavenDb();
#else
    // The store: in memory, so the app needs no database. Data resets on every restart.
    spark.UseInMemory();
#endif
    spark.UseContext<SparkAppContext>();
#if (UseMigrations)

    // Runs every migration (yours under Migrations/, and those the referenced packages ship) once, at startup.
    spark.AddMigrations();
#endif
#if (HasAuth)

    // Password accounts. Groups and rights live in App_Data/security.json.
    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
    });
#endif
#if (HasOidc)
    // External sign-in providers from Spark:Auth:Providers (appsettings.json).
    spark.AddExternalProviders(builder.Configuration);
#endif
#if (UseMessaging)

    // A durable message bus on RavenDB, configured from Spark:Messaging.
    spark.AddMessaging();
#endif
#if (UseMail)
    // Outgoing mail through Messaging, configured from Spark:Mail. appsettings.Development.json writes .eml files.
    spark.AddMailManager();
#endif
#if (UseCron)
    // Generated: registers every ISparkCronJob in this project (see Jobs/).
    spark.AddCronJobs();
#endif
#if (UseSoftDelete)
    spark.AddSoftDelete();
#endif
#if (UseHistory)
    spark.AddHistory();
#endif
#if (UseMcp)
    spark.AddMcp(mcp =>
    {
        mcp.ServerName = "SparkApp";
        mcp.ToolPrefix = "app";
    });
#endif
#if (UseLogging)
    spark.AddLogs();
#endif
#if (UseControllers)

    // Controllers are mounted through Spark, so they share its authentication, antiforgery and [SparkAuthorize].
    spark.AddControllers();
    spark.UseControllers();
#endif
});
#if (HasClient)

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});
#endif

// Build steps, not run modes: each writes or checks a file under App_Data and returns before Build().
//   --spark-synchronize-model     App_Data/Model/*.json from the entity classes
//   --spark-synchronize-security  a starting App_Data/security.json (never overwrites)
//   --spark-verify-security       fails when the rights reachable without signing in have changed
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;
if (builder.InitializeSparkSecurityIfRequested(args))
    return;
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
#if (HasClient)
app.UseSpaStaticFilesImproved();
#endif

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
});
#if (HasClient)

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            // In Development the host starts the Angular dev server itself (npm start) and proxies to it:
            // `dotnet run` is the whole command.
            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [OpenBrowserRegex()]);
            }
        });
    });
#endif

app.Run();
#if (HasClient)

partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex OpenBrowserRegex();
}
#endif
