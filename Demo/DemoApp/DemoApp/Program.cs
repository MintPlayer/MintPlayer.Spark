using DemoApp;
using Microsoft.AspNetCore.HttpOverrides;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Messaging;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add services to the container.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<DemoSparkContext>();

    // Mounted through Spark rather than with endpoints.MapControllers(), so the controllers
    // share Spark's pipeline — its authentication schemes, its antiforgery scope, and
    // [SparkAuthorize]. A bare MapControllers() is reported by SPARK010.
    spark.AddControllers();
    spark.UseControllers();

    spark.AddActions();
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddCronJobs();
    // DemoApp has no authorization model — opt into the permissive
    // IAccessControl explicitly. Removing this line falls back to the
    // deny-all default and every Spark request is refused.
    spark.AllowAnonymousAccess();
});

// Configure SPA static files
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

var app = builder.Build();

app.UseForwardedHeaders();

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