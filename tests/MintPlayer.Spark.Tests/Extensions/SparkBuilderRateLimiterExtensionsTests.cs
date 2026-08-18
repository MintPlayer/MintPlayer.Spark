using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// Tests for <see cref="SparkBuilderRateLimiterExtensions.AddRateLimiter"/>, at two levels.
/// <para>
/// The wiring shape — services registered, options defaults, builder chaining — is observable
/// straight from DI. Path <em>scoping</em> is not: which requests are metered is decided per request
/// inside the partition factory, so those tests boot a `TestServer` pipeline carrying only the
/// limiter and read the status codes back (see <c>MeasureScopeAsync</c>).
/// </para>
/// </summary>
public class SparkBuilderRateLimiterExtensionsTests
{
    [Fact]
    public void AddRateLimiter_registers_RateLimiter_options_in_DI()
    {
        var builder = new TestBuilder();

        builder.AddRateLimiter();

        using var provider = builder.Services.BuildServiceProvider();
        // ASP.NET's AddRateLimiter wires IOptions<RateLimiterOptions> + the middleware services.
        provider.GetService<IOptions<RateLimiterOptions>>().Should().NotBeNull();
    }

    [Fact]
    public void AddRateLimiter_uses_documented_defaults_when_no_configurator_is_supplied()
    {
        var builder = new TestBuilder();
        var captured = new SparkRateLimiterOptions();

        // Re-run with an explicit no-op so we can compare against the bare defaults exposed
        // by the public options class. This pins the contract that AddRateLimiter() and
        // AddRateLimiter(_ => { }) are equivalent.
        builder.AddRateLimiter(o => captured = o);

        captured.PermitLimit.Should().Be(150);
        captured.Window.Should().Be(TimeSpan.FromSeconds(10));
        captured.PathPrefixes.Should().Equal("/spark", "/connect");
    }

    [Theory]
    // The three shapes a caller might reasonably write. StartsWithSegments only matches the
    // middle one, so the extension has to normalize rather than make the caller know that.
    [InlineData("api/browse")]
    [InlineData("/api/browse")]
    [InlineData("/api/browse/")]
    public async Task PathPrefixes_are_normalized_so_slash_placement_does_not_change_the_scope(string configured)
    {
        var (metered, unmetered) = await MeasureScopeAsync(
            options =>
            {
                options.PathPrefixes = [configured];
                options.PermitLimit = 1;
            },
            pathA: "/api/browse/report",
            pathB: "/other");

        metered.Should().Contain(HttpStatusCode.TooManyRequests, "the configured prefix must be metered");
        unmetered.Should().NotContain(HttpStatusCode.TooManyRequests, "paths outside it must not be");
    }

    [Fact]
    public async Task Assigning_PathPrefixes_replaces_the_defaults_rather_than_adding_to_them()
    {
        // Documented on the property, and worth pinning: an app that lists only its own surface
        // must not silently keep /spark metered, or the budget it thinks it configured is shared
        // with traffic it never named.
        var (metered, sparkPath) = await MeasureScopeAsync(
            options =>
            {
                options.PathPrefixes = ["/api"];
                options.PermitLimit = 1;
            },
            pathA: "/api/browse",
            pathB: "/spark/anything");

        metered.Should().Contain(HttpStatusCode.TooManyRequests);
        sparkPath.Should().NotContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task All_prefixes_share_one_bucket_per_caller()
    {
        // D1: the budget is a per-caller allowance, not a per-route one. Two requests against two
        // different metered prefixes must consume the same single permit.
        var (first, second) = await MeasureScopeAsync(
            options =>
            {
                options.PathPrefixes = ["/spark", "/api"];
                options.PermitLimit = 1;
            },
            pathA: "/spark/one",
            pathB: "/api/two",
            requestsPerPath: 1);

        first.Should().NotContain(HttpStatusCode.TooManyRequests, "the first request spends the only permit");
        second.Should().Contain(HttpStatusCode.TooManyRequests,
            "a different prefix draws on the same bucket, so the permit is already gone");
    }

    /// <summary>
    /// Every way of writing "no prefixes": absent, blank, whitespace, and a bare root — which would
    /// normalize to the empty string and therefore mean "meter everything", not "meter /".
    /// </summary>
    public static TheoryData<string[]> EmptyPrefixCases()
    {
        var cases = new TheoryData<string[]>();
        cases.Add([]);
        cases.Add([""]);
        cases.Add(["   "]);
        cases.Add(["/"]);
        return cases;
    }

    [Theory]
    [MemberData(nameof(EmptyPrefixCases))]
    public void Empty_PathPrefixes_throws_rather_than_metering_nothing(string[] prefixes)
    {
        var builder = new TestBuilder();

        var act = () => builder.AddRateLimiter(options => options.PathPrefixes = prefixes);

        // A limiter scoped to no paths is a security control that silently does nothing — the one
        // outcome worse than a startup error.
        act.Should().Throw<ArgumentException>()
           .WithMessage("*PathPrefixes*");
    }

    [Fact]
    public void AddRateLimiter_invokes_caller_configurator_with_the_options_object()
    {
        var builder = new TestBuilder();
        var seen = false;

        builder.AddRateLimiter(options =>
        {
            seen = true;
            options.PermitLimit = 42;
            options.Window = TimeSpan.FromMinutes(1);
        });

        seen.Should().BeTrue();
    }

    [Fact]
    public void AddRateLimiter_returns_the_builder_for_chaining()
    {
        var builder = new TestBuilder();

        var returned = builder.AddRateLimiter();

        returned.Should().BeSameAs(builder);
    }

    [Fact]
    public async Task AddRateLimiter_queues_a_middleware_action_that_runs_without_throwing()
    {
        // Real ASP.NET app builder — proves the queued middleware action invokes
        // UseRateLimiter() against a configured pipeline. If AddRateLimiter forgot to
        // wire RateLimiter services, UseRateLimiter would throw at host start.
        var builder = new TestBuilder();
        builder.AddRateLimiter();

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    foreach (var d in builder.Services) s.Add(d);
                });
                web.Configure(app => builder.Registry.ApplyMiddleware(app));
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>
    /// Boots a minimal host carrying only the limiter's middleware, fires a burst at two paths, and
    /// returns the status codes each saw. Path scoping is a per-request decision made inside the
    /// partition factory, so it is not observable from DI — it needs a real pipeline.
    /// </summary>
    private static async Task<(List<HttpStatusCode> A, List<HttpStatusCode> B)> MeasureScopeAsync(
        Action<SparkRateLimiterOptions> configure,
        string pathA,
        string pathB,
        int requestsPerPath = 3)
    {
        var builder = new TestBuilder();
        builder.AddRateLimiter(configure);

        using var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    foreach (var d in builder.Services) s.Add(d);
                });
                web.Configure(app =>
                {
                    builder.Registry.ApplyMiddleware(app);
                    // Terminal 200 so anything the limiter admits is distinguishable from a 429.
                    app.Run(context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        return Task.CompletedTask;
                    });
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            var client = host.GetTestClient();

            // Sequential on purpose: a fixed window with PermitLimit=1 gives a deterministic
            // first-succeeds-then-429 sequence only if the requests are ordered.
            var a = await BurstAsync(client, pathA, requestsPerPath);
            var b = await BurstAsync(client, pathB, requestsPerPath);
            return (a, b);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<List<HttpStatusCode>> BurstAsync(HttpClient client, string path, int count)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var response = await client.GetAsync(path);
            codes.Add(response.StatusCode);
        }

        return codes;
    }

    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }
}
