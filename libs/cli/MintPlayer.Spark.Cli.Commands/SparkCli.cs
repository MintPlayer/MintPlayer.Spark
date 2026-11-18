using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.CliGenerator.Attributes;
using MintPlayer.Spark.Cli.Services;
using Spectre.Console;

namespace MintPlayer.Spark.Cli;

/// <summary>The entry point both tool hosts call: <c>spark</c> passes its arguments, <c>create-spark-app</c> prepends <c>new</c>.</summary>
public static class SparkCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddSparkCommand()
            .AddSingleton<ProcessRunner>()
            .AddSingleton<Probes>()
            .AddSingleton<TemplateInstaller>()
            .AddSingleton<ModelSynchronizer>()
            .AddSingleton<AppCreator>()
            .AddSingleton<ItemAdder>();
        using var app = builder.Build();

        try
        {
            return await app.InvokeSparkCommandAsync(args);
        }
        catch (ParseCommandException e)
        {
            foreach (var error in e.Errors) AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
            return 2;
        }
    }
}
