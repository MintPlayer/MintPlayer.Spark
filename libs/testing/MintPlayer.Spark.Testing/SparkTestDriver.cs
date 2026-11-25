namespace MintPlayer.Spark.Testing;

/// <summary>
/// The provider-neutral base class for a Spark test that needs a database of its own per test case (#536 D35).
/// The database type is the one the test assembly's <see cref="SparkTestDatabaseAttribute"/> names (a provider
/// testing package writes it), or <c>SPARK_TEST_DATABASE</c>'s pick when it names several: see
/// <see cref="SparkTestDatabases"/>. A test on this driver boots its host with
/// <c>new SparkEndpointFactory&lt;TContext&gt;(Database, models)</c>, seeds through <c>factory.SeedAsync</c>, and so runs
/// on RavenDB, SQL Server, PostgreSQL and InMemory without a line of provider code.
/// </summary>
/// <remarks>
/// A test that pins one database's behaviour stays on that provider's driver (<c>RavenSparkTestDriver</c>). For a
/// database per test class, use <see cref="SparkSharedDatabase"/> and <see cref="SparkSharedTestDriver"/>.
/// </remarks>
public abstract class SparkTestDriver : IAsyncLifetime
{
    private ISparkTestDatabase? database;

    /// <summary>This assembly's database type.</summary>
    protected ISparkTestDatabaseProvider Provider => SparkTestDatabases.For(GetType().Assembly);

    /// <summary>This case's own database, created in <see cref="InitializeAsync"/> and dropped in <see cref="DisposeAsync"/>.</summary>
    protected ISparkTestDatabase Database
        => database ?? throw new InvalidOperationException("The database is created in InitializeAsync; a constructor cannot use it.");

    public virtual async Task InitializeAsync() => database = await Provider.CreateDatabaseAsync();

    public virtual async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }
}
