using System.Text.RegularExpressions;
using Fleet;
using MintPlayer.AspNetCore.SpaServices.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSparkFull(builder.Configuration, options =>
{
    options.Replication = opt =>
    {
        var section = builder.Configuration.GetSection("SparkReplication");
        opt.ModuleName = section["ModuleName"] ?? "Fleet";
        opt.ModuleUrl = section["ModuleUrl"] ?? "https://localhost:5003";
        opt.SparkModulesUrls = section.GetSection("SparkModulesUrls").Get<string[]>() ?? ["http://localhost:8080"];
        opt.SparkModulesDatabase = section["SparkModulesDatabase"] ?? "SparkModules";
        opt.AssembliesToScan = [typeof(Fleet.Replicated.Person).Assembly];
    };
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
app.UseSparkFull(args);

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSparkFull();
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
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
            }
        });
    });

app.Run();
