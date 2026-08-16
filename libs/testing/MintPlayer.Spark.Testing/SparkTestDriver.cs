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
/// RavenDB 7.x requires a license even for the embedded TestDriver. We load it from:
///   1. <c>RAVENDB_LICENSE</c> env var (CI-friendly, JSON content)
///   2. <c>raven-license.log</c> at the repository root (local development)
///
/// If neither is present, tests that derive from this class will fail at
/// <see cref="InitializeAsync"/> with a clear message — see <see cref="LicenseHelper"/>.
/// </summary>
public abstract class SparkTestDriver : RavenTestDriver, IAsyncLifetime
{
    static SparkTestDriver()
    {
        var license = LicenseHelper.LoadOrNull();
        if (license is not null)
        {
            ConfigureServer(new TestServerOptions
            {
                Licensing = new ServerOptions.LicensingOptions
                {
                    License = license,
                    EulaAccepted = true,
                },
            });
        }
    }

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

    public virtual async Task InitializeAsync()
    {
        LicenseHelper.EnsureAvailable();
        Store = GetDocumentStore();

        var assemblies = IndexAssemblies as Assembly[] ?? IndexAssemblies.ToArray();
        if (assemblies.Length > 0)
            await RavenIndexHelper.DeployIndexesAsync(Store, assemblies);
    }

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

    /// <summary>Deploys additional indexes at runtime (e.g., per-test). Also waits for them to settle.</summary>
    protected Task DeployIndexesAsync(params Assembly[] assemblies)
        => RavenIndexHelper.DeployIndexesAsync(Store, assemblies);
}

internal static class LicenseHelper
{
    private const string EnvVar = "RAVENDB_LICENSE";
    private const string LocalFileName = "raven-license.log";

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
