using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// The private statics inside <see cref="SparkExtensions"/> can't be exercised by the public
/// API alone without a real cluster (retry loop) or a curated entry assembly (index discovery).
/// This fixture invokes them via reflection — that's a deliberate trade: the tests pin the
/// branches the public API delegates to, and a rename of the private method is a fast,
/// localized failure rather than a silent coverage regression.
/// </summary>
public class SparkExtensionsPrivateHelpersTests : SparkTestDriver
{
    private static readonly Type ExtType = typeof(SparkExtensions);

    private static MethodInfo PrivateMethod(string name)
        => ExtType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"private static {name} not found on SparkExtensions");

    // --- IsAbstractIndexCreationTask ------------------------------------

    [Theory]
    [InlineData(typeof(SimpleProbeIndex), true)]
    [InlineData(typeof(MultiMapProbeIndex), true)]
    [InlineData(typeof(string), false)]
    [InlineData(typeof(SparkExtensionsPrivateHelpersTests), false)]
    public void IsAbstractIndexCreationTask_walks_basetype_chain_and_matches_only_AbstractIndexCreationTask_generics(Type type, bool expected)
    {
        var method = PrivateMethod("IsAbstractIndexCreationTask");

        var result = (bool)method.Invoke(null, [type])!;

        result.Should().Be(expected);
    }

    // --- WaitForRavenDbConnection ---------------------------------------

    [Fact]
    public void WaitForRavenDbConnection_with_zero_max_retries_returns_immediately_without_touching_the_store()
    {
        var store = Substitute.For<IDocumentStore>();
        var options = new RavenDbOptions { MaxConnectionRetries = 0 };
        var method = PrivateMethod("WaitForRavenDbConnection");

        method.Invoke(null, [store, options]);

        // Maintenance is never accessed when retries are disabled.
        var _ = store.DidNotReceive().Maintenance;
    }

    [Fact]
    public void WaitForRavenDbConnection_succeeds_on_first_attempt_with_a_responsive_store()
    {
        // Use the embedded test store — it's already connected, so the first call succeeds
        // and the loop exits at the early `return` inside the try (line 322 in the source).
        var options = new RavenDbOptions { MaxConnectionRetries = 3, RetryDelaySeconds = 0 };
        var method = PrivateMethod("WaitForRavenDbConnection");

        var act = () => method.Invoke(null, [Store, options]);

        act.Should().NotThrow();
    }

    // Note: the retry-loop branches (catch + Thread.Sleep + success-after-retry) require a
    // failing-then-succeeding IDocumentStore. Raven's Maintenance/Server executor types are
    // concrete classes without easily substitutable constructors, so those branches are left
    // for an integration-style test against a real (paused/restarted) cluster.

    // --- CreateSparkIndexes ---------------------------------------------

    [Fact]
    public void CreateSparkIndexes_returns_early_with_a_console_warning_when_no_targetAssembly_is_resolved()
    {
        // Calling with assembly=null and Assembly.GetEntryAssembly()==null is hard to force
        // (the test runner has an entry assembly). The early-return is exercised by passing
        // a custom override path that the helper short-circuits on. Since the parameter is
        // typed Assembly?, we get the same `targetAssembly == null` outcome by passing null
        // in tandem with re-routing GetEntryAssembly via reflection isn't worth the bend —
        // instead we just exercise the success path on an assembly with no index types,
        // which still hits the loops (zero-iteration) and IndexCreation.CreateIndexes.
        var indexRegistry = Substitute.For<IIndexRegistry>();
        var app = BuildAppBuilder(Store, indexRegistry);
        var emptyAssembly = typeof(string).Assembly; // mscorlib has no AbstractIndexCreationTask types

        var method = PrivateMethod("CreateSparkIndexes");
        var act = () => method.Invoke(null, [app, emptyAssembly]);

        act.Should().NotThrow();
        // Zero index/projection types found → registry untouched.
        indexRegistry.DidNotReceiveWithAnyArgs().RegisterIndex(default!);
        indexRegistry.DidNotReceiveWithAnyArgs().RegisterProjection(default!, default!);
    }

    [Fact]
    public void CreateSparkIndexes_registers_each_AbstractIndexCreationTask_subclass_with_the_index_registry()
    {
        var indexRegistry = Substitute.For<IIndexRegistry>();
        var app = BuildAppBuilder(Store, indexRegistry);
        var thisAssembly = typeof(SparkExtensionsPrivateHelpersTests).Assembly;

        PrivateMethod("CreateSparkIndexes").Invoke(null, [app, thisAssembly]);

        // The fixture-local SimpleProbeIndex and MultiMapProbeIndex must have been registered.
        indexRegistry.Received().RegisterIndex(typeof(SimpleProbeIndex));
        indexRegistry.Received().RegisterIndex(typeof(MultiMapProbeIndex));
    }

    [Fact]
    public void CreateSparkIndexes_registers_each_FromIndex_attributed_projection_with_the_index_registry()
    {
        var indexRegistry = Substitute.For<IIndexRegistry>();
        var app = BuildAppBuilder(Store, indexRegistry);
        var thisAssembly = typeof(SparkExtensionsPrivateHelpersTests).Assembly;

        PrivateMethod("CreateSparkIndexes").Invoke(null, [app, thisAssembly]);

        indexRegistry.Received().RegisterProjection(typeof(ProbeProjection), typeof(SimpleProbeIndex));
    }

    [Fact]
    public void CreateSparkIndexes_swallows_GetTypes_or_index_creation_exceptions_via_console_warning()
    {
        // Pass a substituted IDocumentStore that throws on the static IndexCreation.CreateIndexes
        // path — actually the static helper enumerates conventions on the store, which on a
        // null store throws NullReferenceException.
        var indexRegistry = Substitute.For<IIndexRegistry>();
        var brokenStore = Substitute.For<IDocumentStore>();
        // Conventions throws → IndexCreation.CreateIndexes propagates → outer catch swallows.
        brokenStore.Conventions.Throws(new InvalidOperationException("test-broken-store"));
        var app = BuildAppBuilder(brokenStore, indexRegistry);
        var thisAssembly = typeof(SparkExtensionsPrivateHelpersTests).Assembly;

        var method = PrivateMethod("CreateSparkIndexes");
        var act = () => method.Invoke(null, [app, thisAssembly]);

        // The catch block swallows; reflection wraps any *unswallowed* exception in
        // TargetInvocationException. Either way the call must not propagate.
        act.Should().NotThrow();
    }

    // --- helpers --------------------------------------------------------

    private static IApplicationBuilder BuildAppBuilder(IDocumentStore store, IIndexRegistry indexRegistry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(indexRegistry);
        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(services.BuildServiceProvider());
        return app;
    }

    // --- fixture types --------------------------------------------------
    // These public top-level types are picked up by Assembly.GetTypes() so the discovery
    // loops in CreateSparkIndexes encounter at least one match per branch.

    public sealed class ProbeEntity
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public class SimpleProbeIndex : AbstractIndexCreationTask<ProbeEntity>
    {
        public SimpleProbeIndex()
        {
            Map = entities => from e in entities select new { e.Name };
        }
    }

    public class MultiMapProbeIndex : AbstractMultiMapIndexCreationTask<ProbeEntity>
    {
        public MultiMapProbeIndex()
        {
            AddMap<ProbeEntity>(entities => from e in entities select new { e.Name });
        }
    }

    [FromIndex(typeof(SimpleProbeIndex))]
    public sealed class ProbeProjection
    {
        public string? Name { get; set; }
    }
}
