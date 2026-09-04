using System.Reflection;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.TestDriver;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// One RavenDB database, shared by every test in a single test class.
/// </summary>
/// <remarks>
/// Use with xUnit's <c>IClassFixture&lt;&gt;</c>, which constructs it once per test class and
/// disposes it when the class finishes. Pair it with <see cref="SparkSharedTestDriver"/>, which
/// gives the test class the same helpers <see cref="SparkTestDriver"/> does:
/// <code>
/// public class CarQueryTests(SparkSharedDatabase database)
///     : SparkSharedTestDriver(database), IClassFixture&lt;SparkSharedDatabase&gt;
/// {
///     [Fact]
///     public async Task Lists_the_cars_it_seeded() { … }
/// }
/// </code>
///
/// <para><b>What this trades, and against what.</b></para>
/// <para>
/// <see cref="SparkTestDriver"/> gives a database per test CASE: total isolation, at the cost of a
/// create/delete cycle each time — several hundred per run in a suite this size — and of a
/// <c>SparkEndpointFactory</c> host that cannot be built once per class because the store it needs
/// does not outlive the case. This gives a database per test CLASS instead. Both drivers ship, and
/// neither replaces the other.
/// </para>
/// <para>
/// The isolation boundary is still a real RavenDB database, so classes cannot see each other and
/// xUnit's default parallelism is untouched. That is deliberate, and it is the whole reason this is
/// not modelled on a single process-wide database: xUnit runs test classes CONCURRENTLY, so a
/// database shared across classes would be entered by several at once. (NUnit is sequential by
/// default, which is why the same design is safe in CronosCore and would not be here.)
/// </para>
///
/// <para><b>⚠️ What you give up: tests in one class share state.</b></para>
/// <para>
/// This is the only new hazard, and it is bounded to the class you can see. A test must not depend
/// on the database being empty, and must not assert on anything it did not put there itself. In
/// particular:
/// </para>
/// <list type="bullet">
/// <item><description><b>No unscoped counts.</b> <c>Query&lt;T&gt;().CountAsync()</c>,
/// <c>SingleAsync()</c> over a collection, <c>Should().BeEmpty()</c> and
/// <c>TotalRecords.Should().Be(3)</c> all become answers about the whole class's data. Assert on
/// the ids you seeded.</description></item>
/// <item><description><b>No reused ids.</b> Two tests both writing <c>people/1</c> now collide.
/// Use <see cref="SparkSharedTestDriver.Id"/>, which prefixes with a per-test scope.</description></item>
/// <item><description><b>Nothing database-wide.</b> <c>StopIndexingOperation</c>, subscription
/// enumeration, compare-exchange on a fixed key, and index-error or entry-count assertions all
/// reach past the test that issued them. Those classes belong on
/// <see cref="SparkTestDriver"/>.</description></item>
/// </list>
/// <para>
/// If a class needs any of the above, that is not a defect in the class — keep it on
/// <see cref="SparkTestDriver"/>. The per-case driver exists for exactly these.
/// </para>
///
/// <para><b>Thread-safety.</b></para>
/// <para>
/// The embedded server is process-wide and already shared by both drivers; see
/// <see cref="SparkEmbeddedServer"/> for why its configuration is a type initialiser. This type
/// itself needs no locking: xUnit constructs one instance per class, awaits
/// <see cref="InitializeAsync"/> before any test in that class runs, and runs the tests of a class
/// sequentially. <see cref="IDocumentStore"/> is thread-safe and intended to be shared;
/// <c>IAsyncDocumentSession</c> is not, which is why every helper here opens its own and none is
/// exposed.
/// </para>
/// </remarks>
public class SparkSharedDatabase : RavenTestDriver, IAsyncLifetime
{
    static SparkSharedDatabase() => SparkEmbeddedServer.EnsureConfigured();

    private readonly List<string> deployedIndexNames = [];

    /// <summary>The store for this class's database. Thread-safe; shared by the class's tests.</summary>
    public IDocumentStore Store { get; private set; } = null!;

    /// <inheritdoc cref="SparkTestDriver.RequireLicense"/>
    protected virtual bool RequireLicense => true;

    /// <inheritdoc cref="SparkTestDriver.IndexAssemblies"/>
    /// <remarks>
    /// Deployed once for the whole class rather than once per test — which is most of the point of
    /// sharing, since building an index into an empty database is otherwise repeated per case.
    /// </remarks>
    protected virtual IEnumerable<Assembly> IndexAssemblies { get; } = [];

    /// <inheritdoc cref="SparkTestDriver.PreInitialize"/>
    protected override void PreInitialize(IDocumentStore documentStore)
    {
        documentStore.Conventions.UseNaturalIds().UseGeneratedIds();
        base.PreInitialize(documentStore);
    }

    public virtual async Task InitializeAsync()
    {
        if (RequireLicense)
            LicenseHelper.EnsureAvailable();

        Store = GetDocumentStore();
        SparkEmbeddedServer.ReportUrls(Store);

        var assemblies = IndexAssemblies as Assembly[] ?? IndexAssemblies.ToArray();
        if (assemblies.Length > 0)
        {
            await RavenIndexHelper.DeployIndexesAsync(Store, assemblies);
            deployedIndexNames.AddRange(RavenIndexHelper.DeclaredIndexNames(assemblies));
        }
    }

    /// <inheritdoc cref="SparkTestDriver.WaitForIndexesAsync"/>
    public Task WaitForIndexesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Store.WaitForIndexingAsync(
            timeout: timeout,
            expectedIndexes: deployedIndexNames,
            cancellationToken: cancellationToken);

    public virtual Task DisposeAsync()
    {
        // Null-guarded for the same reason SparkTestDriver's is: InitializeAsync can fail before
        // assigning Store, and a NullReferenceException here would replace the real failure in the
        // test output.
        if (Store is null)
            return Task.CompletedTask;

        // Same zero-wait delete as the per-case driver. A class-scoped database is deleted far less
        // often, but the teardown that does run is no less able to time out under load — and this is
        // the driver consumers are steered towards for throughput.
        SparkEmbeddedServer.DropDatabaseWithoutWaiting(Store);
        Store.Dispose();
        return Task.CompletedTask;
    }
}
