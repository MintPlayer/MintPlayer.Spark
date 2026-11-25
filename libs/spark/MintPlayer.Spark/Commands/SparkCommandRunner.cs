using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Commands;

namespace MintPlayer.Spark.Commands;

/// <summary>
/// Runs the <see cref="ISparkCommand"/> a command line asks for (#536 D34, proven by S20). The app calls it once, right
/// after <c>Build()</c>: building the host constructs no service and opens no connection, so a command that needs no
/// database runs without one.
/// </summary>
public static class SparkCommandRunner
{
    /// <summary>Lists every registered command with its description.</summary>
    public const string HelpFlag = "--spark-help";

    /// <summary>Exit code for a command line Spark can't run, such as an unknown <c>--spark-</c> flag.</summary>
    internal const int ExitMisconfigured = 2;

    /// <summary>
    /// Runs the command <paramref name="args"/> names and sets <see cref="Environment.ExitCode"/> to its result.
    /// Returns <see langword="true"/> when a command ran and <c>Main</c> should return instead of starting the host.
    /// </summary>
    /// <example><code>
    /// var app = builder.Build();
    /// if (await app.RunSparkCommandIfRequestedAsync(args)) return;
    /// </code></example>
    public static async Task<bool> RunSparkCommandIfRequestedAsync(this IHost app, string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);

        if (await RunAsync(app.Services, args, Console.Out, Console.Error, cancellationToken) is not { } exitCode)
            return false;

        Environment.ExitCode = exitCode;
        return true;
    }

    /// <summary>
    /// The exit code of the command <paramref name="args"/> names, or <see langword="null"/> when they name none.
    /// Only the requested command is constructed, inside a scope (a scoped command resolved from the root provider
    /// throws under <c>ValidateScopes</c>). Help reads the descriptions, which are registered on their own, so it
    /// constructs no command.
    /// </summary>
    internal static async Task<int?> RunAsync(
        IServiceProvider services, IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var flag = args.FirstOrDefault(static a => a.StartsWith(SparkCommandServiceCollectionExtensions.FlagPrefix, StringComparison.Ordinal));
        if (flag is null)
            return null;

        // The last registration of a flag wins, as it does for the keyed command itself.
        var commands = services.GetServices<SparkCommandDescription>()
            .GroupBy(static d => d.Flag, StringComparer.Ordinal)
            .Select(static g => g.Last())
            .OrderBy(static d => d.Flag, StringComparer.Ordinal)
            .ToList();

        if (flag == HelpFlag)
        {
            var width = commands.Count == 0 ? 0 : commands.Max(static d => d.Flag.Length);
            await output.WriteLineAsync("Spark commands (run one, then the process exits):");
            foreach (var command in commands)
                await output.WriteLineAsync($"  {command.Flag.PadRight(width)}  {command.Description}");
            await output.WriteLineAsync($"  {HelpFlag.PadRight(width)}  Lists these commands.");
            return 0;
        }

        if (!commands.Any(d => d.Flag == flag))
        {
            // A typo must not start the web server instead: every --spark- flag is a command.
            await error.WriteLineAsync($"Unknown Spark command '{flag}'. Run with {HelpFlag} to list the commands.");
            return ExitMisconfigured;
        }

        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredKeyedService<ISparkCommand>(flag).RunAsync(args, cancellationToken);
    }
}
