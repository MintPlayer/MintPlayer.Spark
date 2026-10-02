using CodeCoverage.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.TestDriver;

namespace CodeCoverage.Tests;

/// <summary>
/// Base class for every test that needs an embedded RavenDB.
///
/// <see cref="RavenTestDriver.ConfigureServer"/> configures a **process-wide**
/// server and throws once any store has been created — so it must run exactly
/// once per test assembly, before the first <c>GetDocumentStore</c>. Each test
/// class calling it in its own static constructor happens to work only while
/// the class that runs first is also the one that configures; xUnit runs
/// classes in parallel, so which one that is varies by machine. It passed
/// locally and failed in CI on the same commit.
///
/// Hoisting it here makes the ordering irrelevant: the base type initializer
/// runs before any derived instance exists, and there is only one of it.
/// Derive from this rather than from <see cref="RavenTestDriver"/> directly —
/// then a new test class cannot re-arm the problem by forgetting, or by
/// remembering.
/// </summary>
public abstract class CoverageRavenTest : RavenTestDriver
{
    /// <summary>
    /// The licence, as JSON, matching the convention <c>MintPlayer.Spark.Testing</c>
    /// already uses. CI supplies it from the organization secret of the same name.
    /// </summary>
    private const string LicenseEnvironmentVariable = "RAVENDB_LICENSE";

    static CoverageRavenTest()
    {
        // Before the server starts: it inherits the variable. See the method.
        MintPlayer.Spark.Testing.RavenServerLocator.DisableServerDynamicPgo();

        var license = Environment.GetEnvironmentVariable(LicenseEnvironmentVariable);

        ConfigureServer(new TestServerOptions
        {
            // The server is no longer copied into bin/ — it would be a 623 MB build output and
            // Nx caches bin/Debug. Provisioned once into a temp directory instead; null leaves
            // RavenDB's default (AppContext.BaseDirectory) in place.
            ServerDirectory = MintPlayer.Spark.Testing.RavenServerLocator.ServerDirectory,

            // Deliberately conditional rather than always tolerant.
            //
            // With a licence, honour it and let an invalid one fail loudly at
            // startup — `ThrowOnInvalidOrMissingLicense = false` would turn a
            // misconfigured licence into a silent downgrade to restricted mode,
            // and the failure would then surface as an obscure "feature not
            // available" inside whichever test first needs a licensed feature,
            // rather than as "the licence is wrong" where it can be fixed.
            //
            // Without one, start restricted rather than refusing to run: org
            // secrets are not exposed to pull requests from forks, and nothing
            // in this suite needs a licensed feature. A contributor without a
            // licence gets a running suite, not a wall.
            Licensing = string.IsNullOrWhiteSpace(license)
                ? new Raven.Embedded.ServerOptions.LicensingOptions
                {
                    ThrowOnInvalidOrMissingLicense = false,
                }
                : new Raven.Embedded.ServerOptions.LicensingOptions
                {
                    License = license,
                    EulaAccepted = true,
                },
        });
    }

    /// <summary>
    /// Whether each test store gets every index in the Coverage assembly, which is what
    /// <c>UseSpark()</c> does at startup. Off by default; a class whose code under test
    /// queries an index overrides it to <c>true</c>.
    ///
    /// Opt-in because deploying them was about half of this suite's run time
    /// (measured 2026-10-01, alone on the owner's laptop: 172 s with indexes on every store,
    /// 88 s without), while only 30 of 73 classes query an index. Forgetting the override
    /// cannot pass silently: queries name their index explicitly, so a store without it
    /// throws IndexDoesNotExistException rather than falling back to an auto-index. All
    /// 206 failures of the run without indexes were exactly that exception.
    /// </summary>
    protected virtual bool DeployIndexes => false;

    protected override void SetupDatabase(IDocumentStore documentStore)
    {
        base.SetupDatabase(documentStore);
        if (DeployIndexes)
            IndexCreation.CreateIndexes(typeof(Commits_ByRepository).Assembly, documentStore);
    }

    /// <summary>
    /// Zero-wait hard delete when each store is disposed. Tests here call
    /// <c>GetDocumentStore()</c> inline (<c>using var store = ...</c>), and the driver runs this hook
    /// for every one of them. Without it the driver's own delete waits the server's hard-coded 15 s
    /// for confirmation. The helper is shared with MintPlayer.Spark.Testing's drivers and linked into
    /// this project by the csproj.
    /// </summary>
    protected override void PreInitialize(IDocumentStore documentStore)
    {
        MintPlayer.Spark.Testing.RavenDatabaseDeletion.DeleteOnDispose(documentStore);
        // No topology cache file per database. See RavenDatabaseDeletion.DisableTopologyCache.
        MintPlayer.Spark.Testing.RavenDatabaseDeletion.DisableTopologyCache(documentStore);
        base.PreInitialize(documentStore);
    }

    /// <summary>
    /// <see cref="RavenTestDriver"/>'s <c>WaitForIndexing</c>, polling every 10 ms instead of 100 ms.
    /// <para>
    /// Hides the driver's method rather than overriding it (it is not virtual), so every call in this
    /// suite, about 150, binds here unchanged. The semantics are the driver's (RavenDB.TestDriver 7.2.6):
    /// done when no enabled index is stale or side-by-side; an errored index or the timeout throws
    /// <see cref="TimeoutException"/> carrying the index errors. Only the poll interval differs: each
    /// wait used to round up to the next 100 ms tick, which this suite pays about 150 times per run,
    /// and much more when a loaded machine stretches every wait.
    /// </para>
    /// </summary>
    protected new void WaitForIndexing(IDocumentStore store, string? database = null, TimeSpan? timeout = null)
    {
        var admin = store.Maintenance.ForDatabase(database);
        var limit = timeout ?? TimeSpan.FromMinutes(1);
        var sp = System.Diagnostics.Stopwatch.StartNew();

        while (sp.Elapsed < limit)
        {
            var statistics = admin.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            var indexes = statistics.Indexes.Where(x => x.State != IndexState.Disabled);

            if (indexes.All(x => !x.IsStale
                && !x.Name.StartsWith(Raven.Client.Constants.Documents.Indexing.SideBySideIndexNamePrefix)))
                return;

            if (statistics.Indexes.Any(x => x.State == IndexState.Error))
                break;

            Thread.Sleep(10);
        }

        var errors = admin.Send(new Raven.Client.Documents.Operations.Indexes.GetIndexErrorsOperation());
        var errorText = errors is { Length: > 0 }
            ? "Indexing errors:\r\n" + string.Join("\r\n", errors.Select(e =>
                $"Index '{e.Name}' ({e.Errors.Length} errors):\r\n" + string.Join("\r\n", e.Errors.Select(x => $"- {x}"))))
            : string.Empty;

        throw new TimeoutException($"The indexes stayed stale for more than {limit}.{errorText}");
    }
}
