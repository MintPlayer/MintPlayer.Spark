using MintPlayer.SourceGenerators.Attributes;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Services;

/// <summary>
/// Runs the app's <c>--spark-synchronize-model</c>, which writes App_Data/Model from the context and the entity
/// classes. The model JSON is never written by hand, so <c>spark add</c> and <c>spark new</c> run the synchronizer
/// rather than emitting it themselves.
/// </summary>
public sealed partial class ModelSynchronizer
{
    [Inject] private readonly ProcessRunner runner;

    /// <summary>Runs it; on failure prints the app's output and returns false.</summary>
    public async Task<bool> RunAsync(string appProject, CancellationToken cancellationToken)
    {
        ProcessResult? result = null;
        await AnsiConsole.Status().StartAsync("Synchronizing the model (dotnet run -- --spark-synchronize-model)…", async _ =>
        {
            result = await runner.RunAsync(
                "dotnet",
                // The SPA builder is off: the synchronizer needs the server only, and npm may not have run yet.
                ["run", "--project", appProject, "--no-launch-profile", "-p:EnableSpaBuilder=false", "--", "--spark-synchronize-model"],
                Path.GetDirectoryName(appProject)!,
                capture: true,
                environment: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
                cancellationToken: cancellationToken);
        });
        if (result!.ExitCode == 0)
        {
            AnsiConsole.MarkupLine("[green]Model synchronized.[/]");
            return true;
        }
        AnsiConsole.MarkupLineInterpolated($"[yellow]The model synchronizer failed (exit {result.ExitCode}). Its output:[/]");
        AnsiConsole.WriteLine(result.Output);
        return false;
    }
}
