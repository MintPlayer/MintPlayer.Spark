using System.Text.RegularExpressions;
using Fleet;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<FleetContext>();
    spark.AddActions();
    spark.AddCustomActions();

    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();

    spark.AddMessaging();

    spark.AddReplication(opt =>
    {
        var section = builder.Configuration.GetSection("SparkReplication");
        opt.ModuleName = section["ModuleName"] ?? "Fleet";
        opt.ModuleUrl = section["ModuleUrl"] ?? "https://localhost:5003";
        opt.SparkModulesUrls = section.GetSection("SparkModulesUrls").Get<string[]>() ?? ["http://localhost:8080"];
        opt.SparkModulesDatabase = section["SparkModulesDatabase"] ?? "SparkModules";
        opt.AssembliesToScan = [typeof(Fleet.Replicated.Person).Assembly];
    });
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.Fleet";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();
app.SynchronizeSparkModelsIfRequested<FleetContext>(args);

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

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
            }
        });
    });

app.Run();
