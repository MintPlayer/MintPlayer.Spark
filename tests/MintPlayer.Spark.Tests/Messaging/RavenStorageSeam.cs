using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// The storage seam over a test's RavenDB store, for tests that construct messaging services by hand (#536 D40: they
/// take <see cref="ISparkStore"/> and <see cref="ISparkAtomicValues"/>, never an <see cref="IDocumentStore"/>).
/// </summary>
/// <summary>
/// A RavenDB fixture for messaging: messaging's view (<see cref="Spark.Messaging.Models.SparkMessages_ByState"/>, D42) is
/// deployed and caught up before the first test, as <c>UseSpark</c> deploys it for an app.
/// </summary>
public abstract class MessagingRavenTestDriver : MintPlayer.Spark.Testing.RavenSparkTestDriver
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _ = Store.SparkStore();
        await Store.WaitForIndexingAsync(expectedIndexes: [nameof(Spark.Messaging.Models.SparkMessages_ByState)]);
    }
}

internal static class RavenStorageSeam
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDocumentStore, object> deployed = [];

    /// <remarks>
    /// Deploys messaging's view (D42) once per store, as <c>UseSpark</c> does for an app: the feeder's page and the
    /// sweeper query it.
    /// </remarks>
    public static IServiceProvider For(IDocumentStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddLogging(); // the index deployment logs (#716 D3)
        services.AddRavenDbSessions();
        var provider = services.BuildServiceProvider();
        deployed.GetValue(store, _ =>
        {
            provider.GetRequiredService<ISparkIndexDeployment>().Deploy([typeof(Spark.Messaging.Models.SparkMessage).Assembly], null);
            return new object();
        });
        return provider;
    }

    public static ISparkStore SparkStore(this IDocumentStore store) => For(store).GetRequiredService<ISparkStore>();

    public static ISparkSubscriptions SparkSubscriptions(this IDocumentStore store) => For(store).GetRequiredService<ISparkSubscriptions>();

    public static ISparkAtomicValues AtomicValues(this IDocumentStore store) => For(store).GetRequiredService<ISparkAtomicValues>();

    public static MessagingLeaseManager HeldLease(this IDocumentStore store)
        => new(store.AtomicValues(), NullLogger<MessagingLeaseManager>.Instance) { IsHeld = true };
}
