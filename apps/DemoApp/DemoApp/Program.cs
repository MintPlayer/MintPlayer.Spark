using DemoApp;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Commands;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Extensions;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    // The store (#551 D14): in memory, so the demo needs no database. Data resets on restart; DemoSeed
    // writes the starting records. For RavenDB (indexes, messaging, cron), see the HR and Fleet apps.
    spark.UseInMemory(store => store.Seed(DemoSeed.WriteAsync));

    spark.UseContext<DemoSparkContext>();

    // Mounted through Spark rather than with endpoints.MapControllers(), so the controllers
    // share Spark's pipeline — its authentication schemes, its antiforgery scope, and
    // [SparkAuthorize]. A bare MapControllers() is reported by SPARK010.
    spark.AddControllers();
    spark.UseControllers();

    spark.AddActions();
});

// Configure SPA static files
builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

// Spark's commands (--spark-help lists them): model synchronization, security, and every package's own. Building the
// host constructs no service and opens no connection, so the ones that need no database run in CI.
if (await app.RunSparkCommandIfRequestedAsync(args))
    return;

// Configure the HTTP request pipeline.
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