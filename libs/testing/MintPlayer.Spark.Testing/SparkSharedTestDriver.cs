using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Base class for tests that share one database across the test class, rather than taking a fresh
/// one per test case.
/// </summary>
/// <remarks>
/// Pair with <see cref="SparkSharedDatabase"/> through xUnit's <c>IClassFixture&lt;&gt;</c>:
/// <code>
/// public class CarQueryTests(SparkSharedDatabase database)
///     : SparkSharedTestDriver(database), IClassFixture&lt;SparkSharedDatabase&gt;
/// {
///     [Fact]
///     public async Task Lists_the_cars_it_seeded()
///     {
///         await SeedAsync(s =&gt; s.StoreAsync(new Car { Plate = "ABC" }, Id("cars/1")));
///
///         var car = await Store.OpenAsyncSession().LoadAsync&lt;Car&gt;(Id("cars/1"));
///
///         car.Plate.Should().Be("ABC");
///     }
/// }
/// </code>
///
/// <para><b>Why this does not derive from <c>RavenTestDriver</c>.</b></para>
/// <para>
/// <c>RavenTestDriver</c> ties a store's lifetime to the instance that created it, and xUnit builds
/// a fresh instance of a test class for every case — so deriving from it forces a database per
/// case, which is exactly what <see cref="SparkTestDriver"/> is for. Here the fixture owns the
/// database and this class only borrows it, which is what lets one database serve many test cases
/// (and, just as importantly, lets a <c>SparkEndpointFactory</c> be built once for the class
/// instead of once per case).
/// </para>
///
/// <para><b>⚠️ Read <see cref="SparkSharedDatabase"/> before using this.</b></para>
/// <para>
/// Tests in the class see each other's documents. No unscoped counts, no reused ids, nothing
/// database-wide. When any of those is what a class needs, keep it on
/// <see cref="SparkTestDriver"/> — that is not a workaround, it is what the per-case driver exists
/// for.
/// </para>
/// </remarks>
public abstract class SparkSharedTestDriver
{
    private readonly SparkSharedDatabase database;

    protected SparkSharedTestDriver(SparkSharedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary>The class's shared store. Thread-safe; do not dispose it — the fixture owns it.</summary>
    protected IDocumentStore Store => database.Store;

    /// <summary>
    /// A prefix unique to this test case, for building ids that cannot collide with another test in
    /// the same class.
    /// </summary>
    /// <remarks>
    /// One per test case: xUnit builds a fresh instance of the test class for every <c>[Fact]</c>
    /// and every <c>[Theory]</c> row, so an instance field is exactly test-case scoped. Two rows of
    /// the same theory therefore get different scopes, which is what you want — they would
    /// otherwise write the same ids into the shared database.
    /// <para>
    /// Random rather than derived from the test's name. A name would read better in RavenDB Studio,
    /// but xUnit does not hand the running method to the instance without an analyzer-visible
    /// parameter or an <c>ITestOutputHelper</c>-style injection, and inventing one to decorate an
    /// id is not worth a public constructor parameter on every test class.
    /// </para>
    /// </remarks>
    protected string Scope { get; } = $"t{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Scopes a document id to this test case: <c>Id("people/1")</c> becomes
    /// <c>"t3f9a1c02/people/1"</c>.
    /// </summary>
    /// <remarks>
    /// The scope is a PREFIX rather than a suffix so that ids sort together per test in Studio, and
    /// so a <c>StartsWith(Scope)</c> filter can narrow a query to one test's documents when an
    /// assertion genuinely needs a set rather than a point load.
    /// <para>
    /// This does not help with types that derive their own id (<c>IHasNaturalId</c>) — there the
    /// business key itself has to differ per test.
    /// </para>
    /// </remarks>
    protected string Id(string suffix) => $"{Scope}/{suffix}";

    /// <inheritdoc cref="SparkTestDriver.SeedAsync"/>
    protected async Task SeedAsync(Func<IAsyncDocumentSession, Task> seed, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(seed);

        using var session = Store.OpenAsyncSession();
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout ?? RavenIndexingExtensions.DefaultTimeout,
            throwOnTimeout: true);

        await seed(session);
        await session.SaveChangesAsync();
    }

    /// <inheritdoc cref="SparkTestDriver.SeedFromJsonAsync"/>
    protected Task SeedFromJsonAsync(params string[] relativeOrAbsolutePaths)
    {
        var resolved = relativeOrAbsolutePaths
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p))
            .ToArray();
        return JsonFixtureImporter.ImportAsync(Store, resolved);
    }

    /// <inheritdoc cref="SparkSharedDatabase.WaitForIndexesAsync"/>
    protected Task WaitForIndexesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => database.WaitForIndexesAsync(timeout, cancellationToken);
}
