using System.Text.RegularExpressions;
using DemoApp;
using DemoApp.Services;
using MintPlayer.AspNetCore.Hsts;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Routing;
using MintPlayer.Spark;
using MintPlayer.Spark.Messaging;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, DemoSparkContext>();

// Register all Actions classes (auto-discovered by source generator)
builder.Services.AddSparkActions();

// Register messaging infrastructure and recipients
builder.Services.AddSparkMessaging();
builder.Services.AddSparkRecipients();

builder.Services.AddSpaPrerenderingService<SpaPrerenderingService>();

// Configure SPA static files
builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseImprovedHsts();
app.UseHttpsRedirection();
app.UseStaticFiles();
if (!app.Environment.IsDevelopment())
{
    app.UseSpaStaticFilesImproved();
}

app.UseRouting();
app.UseAuthorization();
app.UseAntiforgery();
app.UseSpark();
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.SynchronizeSparkModelsIfRequested<DemoSparkContext>(args);

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
});

app.MapWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            spa.UseSpaPrerendering(options =>
            {
                options.BootModuleBuilder = app.Environment.IsDevelopment()
                    ? new AngularPrerendererBuilder(npmScript: "build:ssr", @"Build at\:", 1)
                    : null;
                options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
                options.ExcludeUrls = new[] { "/sockjs-node" };
            });

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
            }
        });
    });

app.Run();
