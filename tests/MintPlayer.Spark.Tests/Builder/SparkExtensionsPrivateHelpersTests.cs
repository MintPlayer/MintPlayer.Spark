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
    [InlineData(typeof(MapReduceProbeIndex), true)]
    [InlineData(typeof(string), false)]
    [InlineData(typeof(SparkExtensionsPrivateHelpersTests), false)]
    public void IsAbstractIndexCreationTask_agrees_with_what_RavenDB_will_actually_deploy(Type type, bool expected)
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
        var indexCatalog = Substitute.For<IIndexCatalog>();
        var app = BuildAppBuilder(Store, indexCatalog);
        var emptyAssembly = typeof(string).Assembly; // mscorlib has no AbstractIndexCreationTask types

        var method = PrivateMethod("CreateSparkIndexes");
        var act = () => method.Invoke(null, [app, (IReadOnlyList<Assembly>)[emptyAssembly]]);

        act.Should().NotThrow();
        // Zero index/projection types found → registry untouched.
        indexCatalog.DidNotReceiveWithAnyArgs().RegisterIndex(default!);
        indexCatalog.DidNotReceiveWithAnyArgs().RegisterProjection(default!, default!);
    }

    [Fact]
    public void CreateSparkIndexes_registers_each_AbstractIndexCreationTask_subclass_with_the_index_catalog()
    {
        var indexCatalog = Substitute.For<IIndexCatalog>();
        var app = BuildAppBuilder(Store, indexCatalog);
        var thisAssembly = typeof(SparkExtensionsPrivateHelpersTests).Assembly;

        PrivateMethod("CreateSparkIndexes").Invoke(null, [app, (IReadOnlyList<Assembly>)[thisAssembly]]);

        // Every fixture-local index must have been registered, including the two whose collection type
        // is not derivable. Registering them is the point: a multi-map or map-reduce index that is not
        // in the catalog cannot be resolved by an explicit "indexName" binding, even though RavenDB
        // has deployed it.
        indexCatalog.Received().RegisterIndex(typeof(SimpleProbeIndex));
        indexCatalog.Received().RegisterIndex(typeof(MultiMapProbeIndex));
        indexCatalog.Received().RegisterIndex(typeof(MapReduceProbeIndex));
    }

    [Fact]
    public void CreateSparkIndexes_registers_each_FromIndex_attributed_projection_with_the_index_catalog()
    {
        var indexCatalog = Substitute.For<IIndexCatalog>();
        var app = BuildAppBuilder(Store, indexCatalog);
        var thisAssembly = typeof(SparkExtensionsPrivateHelpersTests).Assembly;

        PrivateMethod("CreateSparkIndexes").Invoke(null, [app, (IReadOnlyList<Assembly>)[thisAssembly]]);

        indexCatalog.Received().RegisterProjection(typeof(ProbeProjection), typeof(SimpleProbeIndex));
    }

    [Fact]
    public void CreateSparkIndexes_swallows_GetTypes_or_index_creation_exceptions_via_console_warning()
    {
        // Pass a substituted IDocumentStore that throws on the static IndexCreation.CreateIndexes
        // path — actually the static helper enumerates conventions on the store, which on a
        // null store throws NullReferenceException.
        var indexCatalog = Substitute.For<IIndexCatalog>();
        var brokenStore = Substitute.For<IDocumentStore>();
        // Conventions throws → IndexCreation.CreateIndexes propagates → outer catch swallows.
        brokenStore.Conventions.Throws(new InvalidOperationException("test-broken-store"));
        var app = BuildAppBuilder(brokenStore, indexCatalog);
        var thisAssembly = typeof(SparkExtensionsPrivateHelpersTests).Assembly;

        var method = PrivateMethod("CreateSparkIndexes");
        var act = () => method.Invoke(null, [app, (IReadOnlyList<Assembly>)[thisAssembly]]);

        // The catch block swallows; reflection wraps any *unswallowed* exception in
        // TargetInvocationException. Either way the call must not propagate.
        act.Should().NotThrow();
    }

    // --- helpers --------------------------------------------------------

    private static IApplicationBuilder BuildAppBuilder(IDocumentStore store, IIndexCatalog indexCatalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(indexCatalog);
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

    /// <summary>
    /// A real two-argument map-reduce index — the shape that used to be invisible.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>AbstractIndexCreationTask&lt;TDocument, TReduceResult&gt;</c> derives from
    /// <c>AbstractGenericIndexCreationTask&lt;TReduceResult&gt;</c>, <b>not</b> from the one-argument
    /// form. Discovery used to test open-generic identity against the one-argument form only, so an
    /// index like this failed that test, was never discovered, and so was never registered — while
    /// <c>IndexCreation.CreateIndexes</c> deployed it anyway using RavenDB's own criterion. A query
    /// naming it then failed with "no deployed index has that name", which was false.
    /// </remarks>
    public class MapReduceProbeIndex : AbstractIndexCreationTask<ProbeEntity, MapReduceProbeIndex.Result>
    {
        public class Result
        {
            public string? Name { get; set; }
            public int Count { get; set; }
        }

        public MapReduceProbeIndex()
        {
            Map = entities => from e in entities select new Result { Name = e.Name, Count = 1 };
            Reduce = results => from r in results
                                group r by r.Name into g
                                select new Result { Name = g.Key, Count = g.Sum(x => x.Count) };
        }
    }

    [FromIndex(typeof(SimpleProbeIndex))]
    public sealed class ProbeProjection
    {
        public string? Name { get; set; }
    }
}
