using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Storage.InMemory;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// InMemory as a test database (#536 D35). No package writes its attribute: InMemory is no database, so a project
/// opts in with <c>[assembly: SparkTestDatabase(typeof(InMemorySparkTestDatabaseProvider))]</c>.
/// </summary>
public sealed class InMemorySparkTestDatabaseProvider : ISparkTestDatabaseProvider
{
    public string Name => "InMemory";

    public Task<ISparkTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ISparkTestDatabase>(new InMemorySparkTestDatabase());
}

/// <summary>One in-memory database, shared by every host the test builds on it.</summary>
public sealed class InMemorySparkTestDatabase : ISparkTestDatabase
{
    private readonly InMemoryDatabase database = new();

    public string ProviderName => "InMemory";

    public void UseProvider(ISparkBuilder spark) => spark.UseInMemory();

    public void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<InMemoryDatabase>();
        services.AddSingleton(database);
    }

    public Task WaitForConsistencyAsync(IServiceProvider services, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
