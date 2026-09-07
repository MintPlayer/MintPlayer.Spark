using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// One booted application, shared by every test in a class.
/// <para>
/// Booting per test was wrong, and failed loudly rather than slowly:
/// <c>InvalidOperationException: Collection was modified; enumeration operation may not execute</c>.
/// Spark holds process-wide state — the module registry, the index catalog and the model loader are
/// not per-host — so two hosts starting concurrently in one test process mutate a collection while
/// the other is enumerating it. xUnit runs test classes in parallel by default, so three tests each
/// constructing a factory is three concurrent boots.
/// </para>
/// <para>
/// A shared host is also simply the right shape: starting the whole composition root is expensive,
/// and none of these tests mutates it. Anything that genuinely needs an isolated host needs its own
/// class, and this comment is the warning that it cannot just new one up alongside these.
/// </para>
/// </summary>
public sealed class CoverageWebHostFixture : CoverageRavenTest, IAsyncLifetime
{
    public IDocumentStore Store { get; private set; } = null!;
    public CoverageWebAppFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Store = GetDocumentStore();
        Factory = new CoverageWebAppFactory(Store);

        // Force the host to start here rather than inside whichever test happens to run first, so a
        // startup failure is attributed to the fixture instead of to an unrelated assertion.
        _ = Factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tears the fixture down in three independent steps, none of which may throw.
    /// </summary>
    /// <remarks>
    /// A fixture teardown fault is reported by xUnit as a <b>Test Class Cleanup Failure</b>, and
    /// that is much worse than it sounds: <c>dotnet test</c> prints
    /// <c>Passed! - Failed: 0, Passed: 378</c> and then <b>exits 1</b>. So the test list is green,
    /// the summary line says everything passed, and the build is red — and under Nx the task simply
    /// reports as failed with the cause nowhere in the output, because only the first line of the
    /// exception survives its log. It cost a CI run to find that the tests were never the problem.
    /// <para>
    /// Both faults seen came from disposal, not from the tests: an <c>AggregateException</c> locally
    /// and a <c>NullReferenceException</c> in CI, intermittently and on unchanged code. Disposing a
    /// host, a store and an embedded RavenDB is exactly where the documented teardown race lives, so
    /// these steps are ordered, guarded, and independent — one failing must not skip the two after
    /// it, since leaving the embedded server undisposed would poison the rest of the assembly.
    /// </para>
    /// <para>
    /// Faults are printed rather than swallowed. Ignoring them silently would trade a confusing red
    /// build for an invisible resource leak, and the point here is only that <b>teardown noise must
    /// not be reported as a test failure</b> — not that it stops mattering.
    /// </para>
    /// </remarks>
    public new async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            try
            {
                await Factory.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CoverageWebHostFixture] host disposal faulted: {ex}");
            }
        }

        try
        {
            Store?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoverageWebHostFixture] store disposal faulted: {ex}");
        }

        try
        {
            base.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoverageWebHostFixture] RavenDB test-driver disposal faulted: {ex}");
        }
    }
}
