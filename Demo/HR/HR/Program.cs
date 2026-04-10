using System.Text.RegularExpressions;
using HR;
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
    spark.UseContext<HRContext>();
    spark.AddActions();

    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();

    spark.AddMessaging();

    spark.AddReplication(opt =>
    {
        var section = builder.Configuration.GetSection("SparkReplication");
        opt.ModuleName = section["ModuleName"] ?? "HR";
        opt.ModuleUrl = section["ModuleUrl"] ?? "https://localhost:5002";
        opt.SparkModulesUrls = section.GetSection("SparkModulesUrls").Get<string[]>() ?? ["http://localhost:8080"];
        opt.SparkModulesDatabase = section["SparkModulesDatabase"] ?? "SparkModules";
        opt.AssembliesToScan = [typeof(HR.Replicated.Car).Assembly];
    });
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.HR";
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
app.UseSpark(o => o.SynchronizeModelsIfRequested<HRContext>(args));

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
