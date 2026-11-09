using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.InMemory;
using MintPlayer.Spark.Storage.InMemory;

namespace MintPlayer.Spark;

/// <summary>What <see cref="SparkInMemoryExtensions.UseInMemory"/> starts the store with.</summary>
public sealed class InMemoryOptions
{
    internal List<Func<ISparkSession, CancellationToken, Task>> Seeds { get; } = [];

    /// <summary>
    /// Adds data written once, when the host starts, in one unit of work of its own: the store is empty on every start.
    /// Seeds run in the order they were added, each committed before the next.
    /// </summary>
    public InMemoryOptions Seed(Func<ISparkSession, CancellationToken, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        Seeds.Add(seed);
        return this;
    }
}

/// <summary>
/// <c>UseInMemory()</c>: the process's memory as the application's store (#551 D14). Documents live in a dictionary,
/// queries run as LINQ-to-objects, and the change feed is in-process; there are no indexes, subscriptions, revisions,
/// attachments, expiration or full-text search. <b>Data resets on restart</b>: for demos, prototypes and tests.
/// </summary>
public static class SparkInMemoryExtensions
{
    /// <summary>Uses an in-memory store, seeded by <paramref name="configure"/>.</summary>
    public static ISparkBuilder UseInMemory(this ISparkBuilder builder, Action<InMemoryOptions>? configure = null)
    {
        var options = new InMemoryOptions();
        configure?.Invoke(options);

        var services = builder.Services;
        services.AddSingleton(options);
        services.TryAddSingleton<InMemoryDatabase>();
        services.AddScoped<ISparkSession>(sp => sp.GetRequiredService<ISparkStore>().OpenSession());

        // Store, query provider and change feed ([Register]ed in this package).
        services.AddSparkInMemoryServices();
        services.AddHostedService<InMemorySeeder>();
        return builder;
    }
}

/// <summary>Announces the store and runs the seeds, before the host serves its first request.</summary>
internal sealed partial class InMemorySeeder : IHostedService
{
    [MintPlayer.SourceGenerators.Attributes.Inject] private readonly ISparkStore store;
    [MintPlayer.SourceGenerators.Attributes.Inject] private readonly InMemoryOptions options;
    [MintPlayer.SourceGenerators.Attributes.Inject] private readonly ILogger<InMemorySeeder> logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("Spark store: in-memory. Data resets on restart.");
        foreach (var seed in options.Seeds)
        {
            using var session = store.OpenSession();
            using (session.IgnoreMaxRequests())
            {
                await seed(session, cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
