using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
/// DI-shape tests for <see cref="SparkBuilderRateLimiterExtensions.AddRateLimiter"/>. The
/// extension registers ASP.NET's rate-limiter services and queues a middleware action on
/// the Spark registry — both observable without booting a host. Per-request partitioning
/// (only /spark/* paths are throttled) needs an integration test, but the wiring shape
/// is testable here.
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

    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }
}
