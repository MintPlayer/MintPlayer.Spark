using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// #693 M11 (D16): <see cref="ISparkSession"/>s over the in-memory store or over a test RavenDB store, through the providers'
/// public registrations, so code written against the storage-agnostic session is tested on both.
/// </summary>
public static class SparkStores
{
    public const string Memory = "memory";
    public const string Raven = "raven";

    public static Xunit.TheoryData<string> Both => [Memory, Raven];

    private sealed class Builder(IServiceCollection services) : ISparkBuilder
    {
        public IServiceCollection Services => services;
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    /// <param name="raven">Opens the test's RavenDB store; called only for <see cref="Raven"/>.</param>
    public static ServiceProvider Provider(string store, Func<IDocumentStore> raven)
    {
        var services = new ServiceCollection().AddLogging();
        if (store == Memory) new Builder(services).UseInMemory();
        else services.AddSingleton(raven()).AddRavenDbSessions();
        return services.BuildServiceProvider();
    }

    /// <summary>Runs <paramref name="work"/> on a session of its own scope, as one message or request would.</summary>
    public static async Task InScopeAsync(this ServiceProvider provider, Func<ISparkSession, Task> work)
    {
        using var scope = provider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<ISparkSession>());
    }
}
