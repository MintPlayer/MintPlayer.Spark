using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.FileSystem;
using SparkEditor;

var builder = WebApplication.CreateBuilder(args);

// Parse CLI arguments for target App_Data paths
var targetPaths = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--target-app-data" && i + 1 < args.Length)
    {
        targetPaths.Add(args[++i]);
    }
}

if (targetPaths.Count == 0)
{
    Console.WriteLine("Usage: SparkEditor --target-app-data <path> [--target-app-data <path2>] [--port <port>]");
    Console.WriteLine("  --target-app-data  Path to target Spark app's App_Data directory");
    Console.WriteLine("  --port            Port to listen on (default: random available port)");
    return;
}

// Parse port
var portArg = Array.IndexOf(args, "--port");
var port = portArg >= 0 && portArg + 1 < args.Length ? int.Parse(args[portArg + 1]) : 0;

if (port > 0)
{
    builder.WebHost.UseUrls($"http://localhost:{port}");
}
else
{
    builder.WebHost.UseUrls("http://localhost:0");
}

// Register the editor file service with target paths
builder.Services.AddSingleton<IReadOnlyList<string>>(targetPaths.AsReadOnly());
builder.Services.AddSingleton<SparkEditor.Services.ISparkEditorFileService, SparkEditor.Services.SparkEditorFileService>();

builder.Services.AddControllers();
builder.Services.AddSpark(spark =>
{
    // Use temp directory for FileSystem storage (the editor's own entity storage)
    var tempPath = Path.Combine(Path.GetTempPath(), "SparkEditor", "data");
    spark.UseFileSystem(tempPath);
    spark.UseContext<SparkEditorContext>();
    spark.AddActions();
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

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

// Report the actual port
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Urls;
    foreach (var address in addresses)
    {
        Console.WriteLine($"Spark Editor listening on: {address}");
    }
});

app.Run();
