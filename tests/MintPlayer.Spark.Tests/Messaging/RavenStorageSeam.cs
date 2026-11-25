using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// The storage seam over a test's RavenDB store, for tests that construct messaging services by hand (#536 D40: they
/// take <see cref="ISparkStore"/> and <see cref="ISparkAtomicValues"/>, never an <see cref="IDocumentStore"/>).
/// </summary>
internal static class RavenStorageSeam
{
    public static IServiceProvider For(IDocumentStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddRavenDbSessions();
        return services.BuildServiceProvider();
    }

    public static ISparkStore SparkStore(this IDocumentStore store) => For(store).GetRequiredService<ISparkStore>();

    public static ISparkSubscriptions SparkSubscriptions(this IDocumentStore store) => For(store).GetRequiredService<ISparkSubscriptions>();

    public static ISparkAtomicValues AtomicValues(this IDocumentStore store) => For(store).GetRequiredService<ISparkAtomicValues>();

    public static MessagingLeaseManager HeldLease(this IDocumentStore store)
        => new(store.AtomicValues(), NullLogger<MessagingLeaseManager>.Instance) { IsHeld = true };
}
