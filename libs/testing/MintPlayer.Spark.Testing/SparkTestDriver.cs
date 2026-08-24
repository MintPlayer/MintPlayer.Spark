using System.Reflection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
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
/// A RavenDB licence lifts the embedded server out of its restricted mode. We load it from:
///   1. <c>RAVENDB_LICENSE</c> env var (CI-friendly, JSON content)
///   2. <c>raven-license.log</c> at the repository root (local development)
///
/// Neither is required. Without one the server starts in RavenDB's AGPL mode — capped at 3 CPU
/// cores, with the licensed features (ETL, encryption, compression, archival, backups) switched
/// off — which still supports store, load, query, update, indexing and subscriptions, so the large
/// majority of this suite runs unchanged. Tests that genuinely need a licensed feature guard
/// themselves with <see cref="RequiresLicensedFeatureAttribute"/>. Only an environment that
/// declares it has a licence, via <c>SPARK_REQUIRE_LICENSE</c>, fails on a missing one — see
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
    /// Whether a missing RavenDB licence fails the fixture. Defaults to whether the environment
    /// declared that it has one, via <c>SPARK_REQUIRE_LICENSE</c> — so absent by default.
    /// <para>
    /// This used to default to <see langword="true"/>, on the reasoning that a licence meant to be
    /// configured and missing should say so rather than fail obscurely later. That reasoning holds
    /// only where a licence could have been configured. It cannot be on a fork pull request —
    /// organization secrets are not exposed to <c>pull_request</c> runs from forks — nor for a
    /// first-time contributor who has not obtained one, so the diagnostic fired hardest at exactly
    /// the people who could do nothing about it, failing every RavenDB test including the large
    /// majority that touch no licensed feature. A licence-less embedded server does support store,
    /// load, query and update — measured, not assumed.
    /// </para>
    /// <para>
    /// The diagnostic is not lost, only moved to where it is actionable: the trusted CI path sets
    /// <c>SPARK_REQUIRE_LICENSE=true</c> and still fails loudly, which is what catches an expired or
    /// rotated secret before it silently downgrades <c>master</c> to a restricted server.
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
    protected virtual bool RequireLicense => LicenseHelper.RequiredByEnvironment;

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
        documentStore.Conventions.UseNaturalIds().UseGeneratedIds();
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

    public virtual Task DisposeAsync()
    {
        // Null-guarded because InitializeAsync can fail before assigning Store — a missing licence,
        // or GetDocumentStore timing out when the shared embedded server is under load. Without
        // the guard this throws a NullReferenceException that REPLACES the real failure in the
        // test output, which is what made those CI timeouts so hard to read.
        Store?.Dispose();
        return Task.CompletedTask;
    }

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
    private const string RequireEnvVar = "SPARK_REQUIRE_LICENSE";

    /// <summary>
    /// Whether the current environment has <em>promised</em> a licence, and so should fail loudly if
    /// one is missing rather than degrading to a restricted server.
    /// </summary>
    /// <remarks>
    /// Absence of a licence is normal — a fork contributor cannot have one, because organization
    /// secrets are not exposed to <c>pull_request</c> runs from forks, and a first-time contributor
    /// running the suite locally has not obtained one either. Neither should hit a wall.
    /// <para>
    /// So the strictness is opted into by the only environment that can guarantee a licence: the
    /// trusted CI path sets <c>SPARK_REQUIRE_LICENSE=true</c>. That is what keeps a rotated or
    /// expired secret from silently downgrading <c>master</c> to a restricted server and quietly
    /// skipping the licensed tests — the free developer licence expires every six months, so silent
    /// degradation is a question of when, not whether.
    /// </para>
    /// </remarks>
    public static bool RequiredByEnvironment =>
        bool.TryParse(Environment.GetEnvironmentVariable(RequireEnvVar), out var required) && required;

    /// <summary>Whether a licence is available from either source.</summary>
    public static bool IsPresent => LoadOrNull() is not null;

    public static string? LoadOrNull()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var fromFile = TryReadRepoRootLicense();
        return fromFile;
    }

    public static void EnsureAvailable()
    {
        if (LoadOrNull() is null)
        {
            throw new InvalidOperationException(
                $"RavenDB license not found, and '{RequireEnvVar}' declares this environment must " +
                $"have one. Set the '{EnvVar}' environment variable to the JSON license content, or " +
                $"place a '{LocalFileName}' file at the repository root. " +
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
