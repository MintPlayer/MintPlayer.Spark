using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// Completes once the store can serve background work (#536 D41): on a store with a schema
/// (<see cref="ISparkSchemaMigrator"/>), after <c>UseSpark</c> has applied it. A hosted service starts before the web
/// host builds its pipeline, so a background service that reads a table on its first line would otherwise race the
/// migration. On a fresh database it found no table, and its <c>ExecuteAsync</c> died. A store without a schema
/// (RavenDB, in-memory) is ready at once.
/// </summary>
public interface ISparkStoreReadiness
{
    /// <summary>Returns when the store is ready; throws what the schema step threw when it failed.</summary>
    Task WaitAsync(CancellationToken cancellationToken = default);
}

/// <summary>The readiness signal (<see cref="ISparkStoreReadiness"/>). <c>UseSpark</c> completes it after the schema step.</summary>
public sealed class SparkStoreReadiness : ISparkStoreReadiness
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SparkStoreReadiness(IServiceProvider services)
    {
        if (services.GetService<ISparkSchemaMigrator>() is null)
            ready.TrySetResult();
    }

    public Task WaitAsync(CancellationToken cancellationToken = default) => ready.Task.WaitAsync(cancellationToken);

    /// <summary>The schema is in place: background work may start.</summary>
    public void SetReady() => ready.TrySetResult();

    /// <summary>The schema step failed: every waiter throws <paramref name="error"/>.</summary>
    public void SetFailed(Exception error) => ready.TrySetException(error);
}

public static class SparkStoreReadinessServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkStoreReadiness"/> once. Core and every feature with a background service call it, so
    /// a container built without <c>AddSpark</c> (a test's) still resolves it.
    /// </summary>
    public static IServiceCollection AddSparkStoreReadiness(this IServiceCollection services)
    {
        services.TryAddSingleton<SparkStoreReadiness>();
        services.TryAddSingleton<ISparkStoreReadiness>(sp => sp.GetRequiredService<SparkStoreReadiness>());
        return services;
    }
}
