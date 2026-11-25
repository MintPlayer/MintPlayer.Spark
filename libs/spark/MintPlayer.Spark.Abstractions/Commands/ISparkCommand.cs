using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions.Commands;

/// <summary>
/// A command-line command of a Spark application, such as <c>--spark-synchronize-model</c> (#536 D34). The DI container
/// is the registry: a package registers each command keyed by its flag with
/// <see cref="SparkCommandServiceCollectionExtensions.AddSparkCommand{TCommand}"/>, and the app's one line after
/// <c>Build()</c>, <c>if (await app.RunSparkCommandIfRequestedAsync(args)) return;</c>, runs the requested one. Adding a
/// package's command edits no app.
/// </summary>
/// <remarks>
/// Only the requested command and its dependencies are constructed, inside a scope. A command that must run without a
/// database (CI runs <c>--spark-synchronize-model</c> with none) takes only services that open no connection when
/// constructed, and only contracts core registers a default for.
/// </remarks>
public interface ISparkCommand
{
    /// <summary>Runs the command and returns the process exit code (0 for success).</summary>
    /// <param name="args">The whole command line, so a command can read its own arguments after its flag.</param>
    Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>A registered command's flag and the line <c>--spark-help</c> prints for it.</summary>
public sealed record SparkCommandDescription(string Flag, string Description);

public static class SparkCommandServiceCollectionExtensions
{
    /// <summary>The prefix every command flag starts with.</summary>
    public const string FlagPrefix = "--spark-";

    /// <summary>
    /// Registers <typeparamref name="TCommand"/> as the command for <paramref name="flag"/>, scoped. When two packages
    /// register the same flag, the last registration wins, so a provider registers after <c>AddSpark</c>.
    /// </summary>
    public static IServiceCollection AddSparkCommand<TCommand>(this IServiceCollection services, string flag, string description)
        where TCommand : class, ISparkCommand
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (string.IsNullOrWhiteSpace(flag) || !flag.StartsWith(FlagPrefix, StringComparison.Ordinal) || flag.Length == FlagPrefix.Length)
            throw new ArgumentException($"A command flag starts with '{FlagPrefix}': '{flag}'.", nameof(flag));

        services.AddKeyedScoped<ISparkCommand, TCommand>(flag);
        // The description is its own singleton, so --spark-help lists every command without constructing one.
        services.AddSingleton(new SparkCommandDescription(flag, description));
        return services;
    }
}
