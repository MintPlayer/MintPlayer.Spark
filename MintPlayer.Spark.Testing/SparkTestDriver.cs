using System.Reflection;
using Raven.Client.Documents;
using Raven.Embedded;
using Raven.TestDriver;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// xUnit-friendly base class for Spark tests that need an in-memory RavenDB instance.
/// Implements <see cref="IAsyncLifetime"/> so setup and disposal run per test class
/// (one instance per test method, by xUnit's default).
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
    /// Assemblies whose <c>AbstractIndexCreationTask</c> types should be deployed automatically
    /// at <see cref="InitializeAsync"/> and waited on for completion. Default: empty. Override
    /// in a subclass to guarantee that every test in the fixture sees its indexes live before
    /// the first <c>[Fact]</c> runs — the Spark equivalent of CronosCore's
    /// <c>IndexHelper.Register + RunAll</c> pattern.
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
        Store.Dispose();
        return Task.CompletedTask;
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
