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

    public new async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        Store.Dispose();
        base.Dispose();
    }
}
