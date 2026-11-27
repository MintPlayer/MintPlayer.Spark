using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// One database AND one booted Spark host for a whole test class, on the test assembly's database type
/// (<c>SPARK_TEST_DATABASE</c>, #536 D35): the provider-neutral <see cref="SharedSparkHost{TContext}"/>.
/// </summary>
/// <remarks>
/// The same rules as <see cref="SharedSparkHost{TContext}"/>: the cases of the class share the database, so each scopes
/// what it writes and asserts only on its own records.
/// </remarks>
public abstract class SparkSharedHost<TContext> : IAsyncLifetime
    where TContext : SparkContext
{
    private ISparkTestDatabase? database;

    /// <summary>The class's database.</summary>
    public ISparkTestDatabase Database
        => database ?? throw new InvalidOperationException("The database is created in InitializeAsync.");

    /// <summary>The class's host. Do not dispose it — the fixture owns it.</summary>
    public SparkEndpointFactory<TContext> Factory { get; private set; } = null!;

    /// <summary>Builds the host against <paramref name="database"/>, once per class.</summary>
    protected abstract SparkEndpointFactory<TContext> CreateFactory(ISparkTestDatabase database);

    public virtual async Task InitializeAsync()
    {
        database = await SparkTestDatabases.For(GetType().Assembly).CreateDatabaseAsync();
        Factory = CreateFactory(database);
    }

    public virtual async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        if (database is not null)
            await database.DisposeAsync();
    }
}
