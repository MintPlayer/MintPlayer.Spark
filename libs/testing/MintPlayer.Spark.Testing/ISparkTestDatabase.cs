using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// One database type for the test harness (#536 D35): it locates or provisions its server once per process and
/// creates databases on it. A provider package (<c>MintPlayer.Spark.Testing.RavenDB</c>, <c>.SqlServer</c>,
/// <c>.PostgreSql</c>) implements it, and its build targets name it in <see cref="SparkTestDatabaseAttribute"/>.
/// </summary>
/// <remarks>Needs a public parameterless constructor: <see cref="SparkTestDatabases"/> creates it from the attribute.</remarks>
public interface ISparkTestDatabaseProvider
{
    /// <summary>The name <c>SPARK_TEST_DATABASE</c> selects it by (<c>RavenDB</c>, <c>SqlServer</c>, …), ignoring case.</summary>
    string Name { get; }

    /// <summary>A new, empty database of its own. The caller disposes it, which drops it.</summary>
    Task<ISparkTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A database a test host runs on. <see cref="SparkEndpointFactory{TContext}"/> calls <see cref="UseProvider"/> inside
/// <c>AddSpark</c> (where an app calls <c>UseRavenDb()</c> or <c>UseSqlServer()</c>) and <see cref="ConfigureServices"/>
/// after it, so the provider can point the host at this database. Several hosts may use one database in turn.
/// </summary>
public interface ISparkTestDatabase : IAsyncDisposable
{
    /// <summary>The provider's <see cref="ISparkTestDatabaseProvider.Name"/>.</summary>
    string ProviderName { get; }

    /// <summary>Registers the store provider, as the app's own composition does.</summary>
    void UseProvider(ISparkBuilder spark);

    /// <summary>Points the registered store at this database; runs after <c>AddSpark</c>.</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Returns once what the host wrote is visible to every query, indexes and views included: RavenDB waits for
    /// its indexes, a relational database and InMemory are consistent at commit.
    /// </summary>
    Task WaitForConsistencyAsync(IServiceProvider services, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
