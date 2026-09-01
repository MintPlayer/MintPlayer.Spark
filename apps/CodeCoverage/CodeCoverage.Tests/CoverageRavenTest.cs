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
        var license = Environment.GetEnvironmentVariable(LicenseEnvironmentVariable);

        ConfigureServer(new TestServerOptions
        {
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
    /// Creates every index in the Coverage assembly on each test store, which is
    /// what <c>UseSpark()</c> does at startup.
    ///
    /// Queries name their index explicitly now that most run through a generated
    /// one, so a store without them does not silently fall back to an auto-index —
    /// it throws IndexDoesNotExistException. Doing it here rather than per test
    /// class means a new test cannot miss it, and a newly added index reaches every
    /// existing test without touching any of them.
    /// </summary>
    protected override void SetupDatabase(IDocumentStore documentStore)
    {
        base.SetupDatabase(documentStore);
        IndexCreation.CreateIndexes(typeof(Commits_ByRepository).Assembly, documentStore);
    }
}
