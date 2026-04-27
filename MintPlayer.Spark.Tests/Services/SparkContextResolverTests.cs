using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// SparkContextResolver hands the per-request RavenDB session to the app's optional
/// <see cref="SparkContext"/> subclass — that's how user query expressions get a Session
/// to call <c>.Query&lt;T&gt;()</c> on. Two contracts: returns null when no SparkContext
/// is registered, and Session is wired before return when one is registered.
/// </summary>
public class SparkContextResolverTests
{
    private sealed class TestSparkContext : SparkContext { }

    [Fact]
    public void ResolveContext_returns_null_when_no_SparkContext_is_registered()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new SparkContextResolver(services);
        var session = Substitute.For<IAsyncDocumentSession>();

        resolver.ResolveContext(session).Should().BeNull();
    }

    [Fact]
    public void ResolveContext_returns_registered_SparkContext_with_Session_wired()
    {
        var ctx = new TestSparkContext();
        var services = new ServiceCollection()
            .AddSingleton<SparkContext>(ctx)
            .BuildServiceProvider();
        var resolver = new SparkContextResolver(services);
        var session = Substitute.For<IAsyncDocumentSession>();

        var resolved = resolver.ResolveContext(session);

        resolved.Should().BeSameAs(ctx);
        resolved!.Session.Should().BeSameAs(session);
    }

    [Fact]
    public void ResolveContext_overwrites_Session_on_each_call_for_same_singleton_context()
    {
        // SparkContext is registered once per app but lives for the request scope's
        // session lifetime; the resolver must rebind on every call so a second request
        // doesn't inherit the prior request's session.
        var ctx = new TestSparkContext();
        var services = new ServiceCollection()
            .AddSingleton<SparkContext>(ctx)
            .BuildServiceProvider();
        var resolver = new SparkContextResolver(services);
        var first = Substitute.For<IAsyncDocumentSession>();
        var second = Substitute.For<IAsyncDocumentSession>();

        resolver.ResolveContext(first);
        resolver.ResolveContext(second);

        ctx.Session.Should().BeSameAs(second);
    }
}
