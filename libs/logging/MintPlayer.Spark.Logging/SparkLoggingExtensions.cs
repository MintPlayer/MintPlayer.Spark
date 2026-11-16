using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Logging.Services;

namespace MintPlayer.Spark.Logging;

/// <summary>
/// Stored logs (<c>docs/guide-logging.md</c>, #660):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark.AddLogs());
/// // appsettings.json: "Logging": { "Document": { "LogLevel": { "Default": "Information" } } }
/// await using (var scope = logScopes.CreateScope("commands/github/123/456", relatedPersistentObjectId: id))
///     scope.Logger.LogInformation("…");
/// </code>
/// </summary>
public static class SparkLoggingExtensions
{
    /// <summary>
    /// Registers <see cref="ILoggerScopeProvider"/> (and <see cref="ILoggerScopeProvider{TCategoryName}"/>) and the
    /// <c>Document</c> logging provider. Works on any Spark store. Reading logs takes <c>Read/Log</c>, which the library
    /// grants nobody, and then the related record's own read gate (<c>LogActions</c>).
    /// </summary>
    public static ISparkBuilder AddLogs(this ISparkBuilder builder)
    {
        AddLogServices(builder.Services);
        return builder;
    }

    /// <summary>Everything <see cref="AddLogs"/> registers; needs an <c>ISparkStore</c> and logging.</summary>
    internal static void AddLogServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LogWriter>();
        services.TryAddSingleton<ILoggerScopeProvider>(sp => new LoggerScopeProvider(
            sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<LogWriter>(),
            sp.GetRequiredService<ILogger<LogWriter>>(), LoggerScopeProvider.DefaultCategory));
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILoggerScopeProvider<>), typeof(LoggerScopeProvider<>)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, DocumentLoggerProvider>());
    }
}
