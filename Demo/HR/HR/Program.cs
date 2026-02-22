using System.Text.RegularExpressions;
using HR;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, HRContext>();

builder.Services.AddSparkAuthorization();
builder.Services.AddSparkAuthentication<SparkUser>();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.HR";
});

builder.Services.AddSparkMessaging();

builder.Services.AddSparkReplication(opt =>
{
    var section = builder.Configuration.GetSection("SparkReplication");
    opt.ModuleName = section["ModuleName"] ?? "HR";
    opt.ModuleUrl = section["ModuleUrl"] ?? "https://localhost:5002";
    opt.SparkModulesUrls = section.GetSection("SparkModulesUrls").Get<string[]>() ?? ["http://localhost:8080"];
    opt.SparkModulesDatabase = section["SparkModulesDatabase"] ?? "SparkModules";
    opt.AssembliesToScan = [typeof(HR.Replicated.Car).Assembly];
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
app.UseAuthentication();
app.UseAuthorization();
app.UseSparkAntiforgery();
app.UseSpark();
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.UseSparkReplication();
app.SynchronizeSparkModelsIfRequested<HRContext>(args);

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
    endpoints.MapSparkReplication();
    endpoints.MapSparkIdentityApi<SparkUser>();
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
