using System.Reflection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions.Database;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;
using Raven.TestDriver;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// xUnit-friendly base class for Spark tests that need an in-memory RavenDB instance.
/// Implements <see cref="IAsyncLifetime"/>, so setup and disposal run <strong>per test
/// case</strong> — xUnit constructs a fresh instance for every <c>[Fact]</c> and every
/// <c>[Theory]</c> row.
/// <para>
/// That granularity is not free: <see cref="InitializeAsync"/> creates a brand-new RavenDB
/// database per test case (<see cref="RavenTestDriver.GetDocumentStore"/> names them
/// <c>InitializeAsync_{N}</c> off a process-wide counter), all on one shared embedded server.
/// Across this suite that is several hundred create/delete cycles per run, so test parallelism
/// is capped in <c>xunit.runner.json</c> — see that file before raising it.
/// </para>
///
/// RavenDB 7.x requires a license even for the embedded TestDriver. We load it from:
///   1. <c>RAVENDB_LICENSE</c> env var (CI-friendly, JSON content)
///   2. <c>raven-license.log</c> at the repository root (local development)
///
/// If neither is present, tests that derive from this class fail at
/// <see cref="InitializeAsync"/> with a message naming both sources. A suite that needs to
/// tolerate that (fork pull requests get no organization secrets) overrides
/// <see cref="RequireLicense"/>.
/// <para>
/// The loader itself is internal — this is deliberately not an extension point, and a consumer
/// following a reference to it would find nothing.
/// </para>
/// </summary>
public abstract class SparkTestDriver : RavenTestDriver, IAsyncLifetime
{
    // Configuring the embedded server is shared with SparkSharedDatabase and must happen exactly
    // once per process, so it lives in SparkEmbeddedServer's type initialiser rather than here.
    static SparkTestDriver() => SparkEmbeddedServer.EnsureConfigured();

    /// <summary>
    /// Whether a missing RavenDB licence fails the fixture. Defaults to <see langword="true"/>.
    /// <para>
    /// The default is the right one for a framework: a licence that was meant to be configured and
    /// is not should say so, naming <c>RAVENDB_LICENSE</c> and <c>raven-license.log</c>, rather than
    /// let a suite run in restricted mode and fail obscurely later.
    /// </para>
    /// <para>
    /// Override to <see langword="false"/> for a suite that must survive without one. The motivating
    /// case is fork pull requests: organization secrets are not exposed to <c>pull_request</c> runs
    /// from forks, so a contributor without a licence otherwise fails every RavenDB test, including
    /// the majority that touch no licensed feature. A licence-less embedded server does support
    /// store, load, query and update — measured, not assumed.
    /// </para>
    /// <para>
    /// This gates <em>this fixture's</em> hard failure, not the server's tolerance — the name reads
    /// like the latter, and it is not. An <b>invalid</b> licence still fails at startup regardless of
    /// this property, because a supplied licence is always validated. Server tolerance is decided once
    /// per process from whether a licence was found at all, so a single test run may freely mix
    /// strict and relaxed fixtures: the strict ones still fail loudly at their own
    /// <see cref="InitializeAsync"/>.
    /// </para>
    /// </summary>
    protected virtual bool RequireLicense => true;

    protected IDocumentStore Store { get; private set; } = null!;

    /// <summary>
    /// Installs Spark's own document-id rules, so a test sees the ids production would assign.
    /// <para>
    /// Without this the suite tested a store it had quietly substituted. <see cref="Store"/> comes
    /// from <see cref="RavenTestDriver.GetDocumentStore"/> and never routes through
    /// <c>AddSpark</c>; <c>SparkEndpointFactory</c> looks like it closes that gap but does not — it
    /// calls <c>AddSpark</c> and then removes the <see cref="IDocumentStore"/> that registered,
    /// putting this one back in its place. So every fixture ran on RavenDB's stock sequential ids
    /// (<c>trailers/1</c>) while production ran on <c>Trailers/{guid}</c>, and
    /// <see cref="IHasNaturalId"/> derivation never ran at all.
    /// </para>
    /// <para>
    /// Conventions freeze at <c>Initialize()</c>, which is why this is the hook: it is the same
    /// point in the lifecycle where <c>AddSpark</c> installs them on the real store.
    /// </para>
    /// </summary>
    protected override void PreInitialize(IDocumentStore documentStore)
    {
        // The same call production makes -- see MintPlayer.Spark.SparkStoreConfiguration.
        documentStore.ApplySparkConventions();
        base.PreInitialize(documentStore);
    }

    /// <summary>
    /// Assemblies whose <c>AbstractIndexCreationTask</c> types should be deployed automatically
    /// at <see cref="InitializeAsync"/> and waited on for completion. Default: empty. Override
    /// in a subclass to guarantee that every test in the fixture sees its indexes live before
    /// the first <c>[Fact]</c> runs.
    /// </summary>
    protected virtual IEnumerable<Assembly> IndexAssemblies { get; } = Array.Empty<Assembly>();

    /// <summary>
    /// Names of the indexes this fixture deployed, so <see cref="WaitForIndexesAsync"/> can insist
    /// they exist rather than accepting the vacuous "no index is stale" that an empty database
    /// always satisfies.
    /// </summary>
    private readonly List<string> _deployedIndexNames = [];

    public virtual async Task InitializeAsync()
    {
        if (RequireLicense)
            LicenseHelper.EnsureAvailable();
        Store = GetDocumentStore();

        var assemblies = IndexAssemblies as Assembly[] ?? IndexAssemblies.ToArray();
        if (assemblies.Length > 0)
            await DeployIndexesAsync(assemblies);
    }

    /// <summary>
    /// Waits until every index this fixture deployed is registered <b>and</b> every index in the
    /// database is up to date.
    /// <para>
    /// Prefer this over calling <see cref="RavenIndexingExtensions.WaitForIndexingAsync"/> on the
    /// store directly: it carries the fixture's declared index names, so a wait cannot pass
    /// because an index it was supposed to be waiting for was never deployed. Note that neither
    /// form can vouch for auto-indexes, which do not exist until a query creates them — RavenDB
    /// blocks on that first creation itself.
    /// </para>
    /// </summary>
    protected Task WaitForIndexesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Store.WaitForIndexingAsync(
            timeout: timeout,
            expectedIndexes: _deployedIndexNames,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Drops the test database without waiting for confirmation, then disposes the store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This exists to stop teardown failing the suite under load.</b> Without it a full local
    /// run loses 20-35 tests out of ~2,500 to
    /// <c>RavenException : TimeoutException: Waited for 00:00:15 for task with index N to complete.
    /// Last commit index is: M</c> — always in teardown, never the same tests twice, and never the
    /// tests that were actually changed.
    /// </para>
    /// <para>
    /// <b>The cause is CPU starvation</b>, established by reproducing it on demand rather than by
    /// reasoning. It needs exactly three ingredients together, and removing any one stops it
    /// reproducing: a saturated machine, concurrent teardowns (four workers, matching
    /// <c>maxParallelThreads: 0.5x</c> on eight cores), and — the one that eluded several attempts —
    /// <b>each database carrying an index, documents and a query</b>. An idle database deletes in
    /// 0.1 s even on a fully starved machine; one with an index takes <b>21.8 s</b>. Deleting is
    /// cheap; deleting something the server is still working on is not.
    /// </para>
    /// <para>
    /// ⚠️ That third ingredient is why this is so easy to "disprove" with a microbenchmark. Creating
    /// and hard-deleting 200 <em>empty</em> databases back to back measures 19 ms at p50 with no
    /// degradation — which says nothing, because it is missing the ingredient that makes deletion
    /// expensive.
    /// </para>
    /// <para>
    /// <c>RavenTestDriver</c>'s own <c>AfterDispose</c> handler sends
    /// <c>DeleteDatabasesOperation(name, hardDelete: true)</c> with no
    /// <c>timeToWaitForConfirmation</c>, and server-side
    /// <c>AdminDatabasesHandler.WaitForDeletionToComplete</c> resolves that to a <b>hard-coded</b>
    /// <c>TimeSpan.FromSeconds(15)</c> — it does <em>not</em> consult
    /// <c>Cluster.OperationTimeoutInSec</c>, so no server configuration can change it. Sending the
    /// operation ourselves first is the only way to set it. The driver's delete then runs anyway and
    /// throws <see cref="DatabaseDoesNotExistException"/>, which its handler explicitly swallows.
    /// </para>
    /// <para>
    /// ⚠️ <b>Approaches already measured and rejected — do not re-propose:</b> skipping the delete
    /// entirely (17% slower overall; the undeleted databases cost more than the deletes saved);
    /// backgrounding <c>Dispose()</c> in a <c>Task.Run</c> (breaks the suite on an idle machine, 89-147
    /// unrelated failures varying per run, because <c>Dispose</c> does more than delete and racing it
    /// against fixture disposal is not safe); serialising deletes behind a <c>SemaphoreSlim</c>
    /// (teardown 16-19 s → 35-47 s; a client-side queue cannot speed up the server's single apply
    /// loop); and catching the timeout rather than preventing it.
    /// </para>
    /// </remarks>
    public virtual async Task DisposeAsync()
    {
        // Null-guarded because InitializeAsync can fail before assigning Store — a missing licence,
        // or GetDocumentStore timing out when the shared embedded server is under load. Without
        // the guard this throws a NullReferenceException that REPLACES the real failure in the
        // test output, which is what made those CI timeouts so hard to read.
        if (Store is null)
            return;

        try
        {
            await Store.Maintenance.Server.SendAsync(
                new DeleteDatabasesOperation(
                    Store.Database,
                    hardDelete: true,
                    fromNode: null,
                    timeToWaitForConfirmation: DatabaseDeletionBudget));
        }
        catch (DatabaseDoesNotExistException)
        {
            // Already gone — nothing to wait for.
        }
        catch (Exception)
        {
            // ⚠️ Never fail a test in teardown over cleanup. The database is in a temp directory on
            // a server that dies with the process, so the worst case of swallowing this is disk we
            // were going to reclaim anyway — whereas throwing here REPLACES the real result of the
            // test that just ran, which is exactly the failure mode this whole change exists to fix.
        }

        Store.Dispose();
    }

    /// <summary>
    /// ⚠️ <b>Zero, and it has to be zero</b> — this is not a short timeout, it is an instruction to
    /// skip the confirmation wait entirely.
    /// <para>
    /// Server-side, <c>WaitForDeletionToComplete</c> computes <c>remaining = timeout - elapsed</c>;
    /// with zero that is already negative, so <c>WaitForIndexNotification</c> is never entered. The
    /// raft command is still submitted and the database is still deleted — only the acknowledgement
    /// is skipped, and the exception is never raised rather than caught.
    /// </para>
    /// <para>
    /// ⚠️ Do not "improve" this into a generous timeout. A longer budget does not make the deletion
    /// faster; it makes teardown <em>block</em> for that long under load instead of failing at 15 s,
    /// which trades a visible failure for an invisible stall. Waiting is the cost being removed.
    /// </para>
    /// </summary>
    private static readonly TimeSpan DatabaseDeletionBudget = TimeSpan.Zero;

    /// <summary>
    /// Writes documents and returns only once RavenDB has indexed them — the deterministic way to
    /// set up a test that then queries.
    /// <para>
    /// This asks the <em>server</em> to hold the write until the indexes covering this transaction
    /// have caught up (<c>WaitForIndexesAfterSaveChanges</c>), which beats the alternative of
    /// saving and then polling every index in the database:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Targeted</b> — only the indexes this write actually touched.</description></item>
    /// <item><description><b>No sampling window</b> — a global poll can observe a momentarily-clean
    /// snapshot and return while a concurrent writer's document is still unindexed. There is no
    /// such gap here: the write itself does not complete until its indexes are current.</description></item>
    /// <item><description><b>Nothing to forget</b> — the guarantee rides on the write, so it cannot
    /// be omitted by the next query someone adds.</description></item>
    /// </list>
    /// <para>
    /// <c>throwOnTimeout</c> is deliberately on. The default is to swallow the timeout and hand
    /// back a write that may not be queryable yet — precisely the silent staleness this exists to
    /// remove.
    /// </para>
    /// <para>
    /// Use <see cref="RavenIndexingExtensions.WaitForIndexingAsync"/> instead when no single
    /// session owns the write: Smuggler imports, and documents written by a background worker or
    /// by the code under test.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// await SeedAsync(session => session.StoreAsync(new Car { Plate = "ABC-123" }));
    /// // query immediately — no WaitForIndexing needed
    /// </code>
    /// </example>
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

    /// <summary>
    /// Imports one or more JSON fixture files into <see cref="Store"/> and waits for indexes
    /// to settle. Relative paths resolve against <see cref="AppContext.BaseDirectory"/> so
    /// fixtures copied to the test output directory via <c>&lt;Content Include="Data\**\*" /&gt;</c>
    /// resolve naturally (e.g. <c>"Data/Seed/people.json"</c>).
    /// </summary>
    protected Task SeedFromJsonAsync(params string[] relativeOrAbsolutePaths)
    {
        var resolved = relativeOrAbsolutePaths
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p))
            .ToArray();
        return JsonFixtureImporter.ImportAsync(Store, resolved);
    }

    /// <summary>
    /// Deploys additional indexes at runtime (e.g. per-test), waits for them to be registered and
    /// settled, and remembers them so later <see cref="WaitForIndexesAsync"/> calls keep checking
    /// they are there.
    /// </summary>
    protected async Task DeployIndexesAsync(params Assembly[] assemblies)
    {
        await RavenIndexHelper.DeployIndexesAsync(Store, assemblies);
        _deployedIndexNames.AddRange(RavenIndexHelper.DeclaredIndexNames(assemblies));
    }
}

internal static class LicenseHelper
{
    private const string EnvVar = "RAVENDB_LICENSE";
    private const string LocalFileName = "raven-license.log";

    /// <summary>
    /// Resolves the licence from <c>RAVENDB_LICENSE</c> — accepting <b>either</b> the JSON itself or
    /// a path to a file containing it — and falls back to a repo-root <c>raven-license.log</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ The path form exists because setting the variable to a path is the obvious thing to do and
    /// used to fail in the worst possible way: the path string was handed to RavenDB *as the
    /// licence*, and a present-but-invalid licence makes the embedded server refuse to start. Every
    /// RavenDB test then failed at once, with an error about licensing rather than about the
    /// variable — so the cause looked like an expired licence rather than a mis-set path.
    /// <para>
    /// CI passes the JSON content directly (a GitHub secret cannot be a file on disk), so both forms
    /// have to work. They are told apart by asking the filesystem, not by parsing: a licence JSON is
    /// never a path that exists.
    /// </para>
    /// </remarks>
    public static string? LoadOrNull()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var trimmed = fromEnv.Trim();

            // Guard the File.Exists call: a JSON licence contains characters that are illegal in a
            // path on Windows, and File.Exists is documented to return false rather than throw for
            // those — but only after a length check, so a long licence would still be fine. Keeping
            // this explicit means a future change cannot turn "looks like JSON" into an exception.
            if (!trimmed.StartsWith('{') && trimmed.Length <= 4096)
            {
                try
                {
                    if (File.Exists(trimmed))
                        return File.ReadAllText(trimmed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Fall through to treating the value as licence content — which will fail with
                    // RavenDB's own licence error, and that is the right error to surface for a
                    // value that is neither readable JSON nor a readable file.
                }
            }

            return fromEnv;
        }

        var fromFile = TryReadRepoRootLicense();
        return fromFile;
    }

    public static void EnsureAvailable()
    {
        if (LoadOrNull() is null)
        {
            throw new InvalidOperationException(
                $"RavenDB license not found. Set the '{EnvVar}' environment variable to the JSON " +
                $"license content, or place a '{LocalFileName}' file at the repository root. " +
                "See https://ravendb.net/buy for community/developer licenses.");
        }
    }

    private static string? TryReadRepoRootLicense()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, LocalFileName);
            if (File.Exists(candidate))
            {
                try
                {
                    return File.ReadAllText(candidate);
                }
                catch
                {
                    return null;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
