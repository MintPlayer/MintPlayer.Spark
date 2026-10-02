using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// One database AND one booted Spark host for a whole test class — an xUnit class fixture, the
/// [OneTimeSetUp]/[OneTimeTearDown] equivalent (M8 item 5).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="SparkEndpointFactory{TContext}"/> boot is the expensive half of most endpoint
/// tests: it writes model files, starts a <c>TestServer</c> and runs Spark's startup gates. With
/// <see cref="SparkTestDriver"/> that happened per test CASE because the store did not outlive the
/// case. Deriving from <see cref="SparkSharedDatabase"/> gives the host a store that does.
/// </para>
/// <para>
/// ⚠️ Only for classes whose cases leave nothing behind that a sibling could observe: no document
/// writes a sibling asserts against, no per-test service swaps (each distinct
/// <c>configureServices</c> is a distinct host and belongs in the test), and no state carried in
/// the host between requests beyond what each test creates for itself (cookies are not: a
/// <c>TestServer</c> client keeps none, so every test threads its own).
/// </para>
/// <para>Usage:</para>
/// <code>
/// public sealed class PeopleHost : SharedSparkHost&lt;TestSparkContext&gt;
/// {
///     protected override SparkEndpointFactory&lt;TestSparkContext&gt; CreateFactory()
///         =&gt; new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
/// }
///
/// public class PeopleTests(PeopleHost host) : SparkSharedTestDriver(host), IClassFixture&lt;PeopleHost&gt;
/// </code>
/// </remarks>
public abstract class SharedSparkHost<TContext> : SparkSharedDatabase
    where TContext : SparkContext
{
    /// <summary>The class's host. Do not dispose it — the fixture owns it.</summary>
    public SparkEndpointFactory<TContext> Factory { get; private set; } = null!;

    /// <summary>Builds the host against <see cref="SparkSharedDatabase.Store"/>, once per class.</summary>
    protected abstract SparkEndpointFactory<TContext> CreateFactory();

    /// <summary>
    /// Runs once, after the database exists and before the host boots: where a class deploys a
    /// static index or seeds data its host must find at startup. Seeds that may follow the boot go
    /// in an <see cref="InitializeAsync"/> override after <c>base.InitializeAsync()</c>.
    /// </summary>
    protected virtual Task BeforeHostAsync() => Task.CompletedTask;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await BeforeHostAsync();
        Factory = CreateFactory();
    }

    public override async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await base.DisposeAsync();
    }
}
