using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commands;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using SqlDemo;

var builder = WebApplication.CreateBuilder(args);

// The #536 demo: Spark on PostgreSQL. The features are the ones the RavenDB demos use, unchanged; only the store differs.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    // The store: PostgreSQL 17 or later, from ConnectionStrings:Spark. At startup Spark applies the framework schema,
    // then the app's migrations (Migrations/PostgreSql, written by --spark-synchronize-model), then the data
    // migrations (D33). Every instance waits for the one that applies them.
    spark.UsePostgreSql();

    spark.UseContext<SqlDemoContext>();
    // Generated: runs the data migrations referenced packages ship (Authorization's) once at startup.
    spark.AddMigrations();

    // Password accounts, stored as rows: an account must confirm its email before it can sign in.
    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
        auth.RequireConfirmedEmail = true;
    });

    // Mail is queued through Messaging (a message table, polled through the change feed) and written as .eml files
    // into Spark:Mail:PickupFolder (appsettings.json): the demo sends nothing.
    spark.AddMessaging();
    spark.AddMailManager();

    // Book's CreatedBy/CreatedAt stamps, and its revisions ("revisions" in App_Data/Model/Book.json).
    spark.AddHistory();
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.SqlDemo";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

// Spark's commands (--spark-help lists them): --spark-synchronize-model writes the model JSON and, for a model change,
// the EF migration; --spark-migrate and --spark-migration-script apply or script the schema (D33).
if (await app.RunSparkCommandIfRequestedAsync(args))
    return;

if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
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
