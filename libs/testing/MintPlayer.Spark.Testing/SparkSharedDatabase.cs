using System.Reflection;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// One database for a whole test class, on the test assembly's database type (#536 D35). Use it as xUnit's
/// <c>IClassFixture&lt;SparkSharedDatabase&gt;</c> with <see cref="SparkSharedTestDriver"/>, which borrows it:
/// <code>
/// public class CarQueryTests(SparkSharedDatabase shared) : SparkSharedTestDriver(shared), IClassFixture&lt;SparkSharedDatabase&gt;
/// </code>
/// Tests in the class see each other's rows: scope what they write (<see cref="SparkSharedTestDriver.Scope"/>).
/// </summary>
/// <remarks>
/// Created on first use rather than in the fixture's own lifecycle, because only the test class knows its assembly,
/// and the assembly names the database type.
/// </remarks>
public sealed class SparkSharedDatabase : IAsyncLifetime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ISparkTestDatabase? database;

    /// <summary>The class's database, created on the first call.</summary>
    public async Task<ISparkTestDatabase> GetAsync(Assembly testAssembly, CancellationToken cancellationToken = default)
    {
        if (database is { } created)
            return created;
        await gate.WaitAsync(cancellationToken);
        try
        {
            return database ??= await SparkTestDatabases.For(testAssembly).CreateDatabaseAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
        gate.Dispose();
    }
}

/// <summary>The base class for tests on a <see cref="SparkSharedDatabase"/>: the class's database, per case.</summary>
public abstract class SparkSharedTestDriver : IAsyncLifetime
{
    private readonly SparkSharedDatabase shared;
    private ISparkTestDatabase? database;

    protected SparkSharedTestDriver(SparkSharedDatabase shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        this.shared = shared;
    }

    /// <summary>The class's database. Disposing is the fixture's job.</summary>
    protected ISparkTestDatabase Database
        => database ?? throw new InvalidOperationException("The database is fetched in InitializeAsync; a constructor cannot use it.");

    /// <summary>A prefix unique to this test case, for the keys and names it writes into the shared database.</summary>
    protected string Scope { get; } = $"t{Guid.NewGuid().ToString("N")[..8]}";

    public virtual async Task InitializeAsync() => database = await shared.GetAsync(GetType().Assembly);

    public virtual Task DisposeAsync() => Task.CompletedTask;
}
