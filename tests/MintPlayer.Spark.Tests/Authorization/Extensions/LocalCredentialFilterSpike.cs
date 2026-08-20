using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// SPIKE S1 — can the endpoints <c>MapIdentityApi&lt;TUser&gt;()</c> produces be selectively
/// suppressed? Microsoft's mapper is all-or-nothing and exposes no removal API, so this
/// probes candidate (a) from the plan: map into a throwaway <see cref="IEndpointRouteBuilder"/>,
/// read the materialized endpoints, and re-publish only the wanted ones.
///
/// Asserts over the endpoint data source, never over HTTP status codes — a 404 only proves
/// unreachability, and the requirement is absence from the route table.
///
/// TEMPORARY: delete once M1 promotes the winning mechanism into the product.
/// </summary>
public class LocalCredentialFilterSpike : SparkTestDriver
{
    /// <summary>Minimal <see cref="IEndpointRouteBuilder"/> that collects data sources without publishing them.</summary>
    private sealed class ThrowawayEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class FixedEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;
    }

    private sealed class NullChangeToken : IChangeToken
    {
        public static readonly NullChangeToken Singleton = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => EmptyDisposable.Singleton;

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Singleton = new();
            public void Dispose() { }
        }
    }

    /// <summary>Spike-only: writes the observed route table to the scratchpad so the evidence can be pasted into the plan.</summary>
    private static void Dump(string name, IReadOnlyList<string> patterns) =>
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), $"spike-s1-{name}.txt"), patterns);

    private async Task<IHost> StartAsync(Action<IEndpointRouteBuilder> map)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => map(endpoints));
                }))
            .StartAsync();
    }

    private static IReadOnlyList<string> PatternsOf(IHost host) =>
        [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public async Task Probe_1_the_unfiltered_surface_is_what_the_census_claims()
    {
        // Establishes the baseline the filter has to cut down, straight from the route table.
        using var host = await StartAsync(endpoints => endpoints.MapGroup("/spark/auth").MapIdentityApi<SparkUser>());

        var patterns = PatternsOf(host);

        // Emitted so the spike record carries the real list, not a remembered one.
        Dump("probe1-unfiltered", patterns);

        patterns.Should().Contain("/spark/auth/register");
        patterns.Should().Contain("/spark/auth/login");
        patterns.Should().Contain("/spark/auth/forgotPassword");
        patterns.Should().Contain("/spark/auth/resetPassword");
        patterns.Should().Contain("/spark/auth/resendConfirmationEmail");
    }

    [Fact]
    public async Task Probe_2_mapping_into_a_throwaway_builder_and_republishing_filtered_removes_them()
    {
        // Candidate (a): the preferred outcome — Microsoft's handlers are kept intact,
        // only the publication is filtered.
        var dropped = new[] { "/register", "/login", "/refresh", "/confirmEmail", "/resendConfirmationEmail", "/forgotPassword", "/resetPassword" };

        using var host = await StartAsync(endpoints =>
        {
            var throwaway = new ThrowawayEndpointRouteBuilder(endpoints.ServiceProvider);
            throwaway.MapGroup("/spark/auth").MapIdentityApi<SparkUser>();

            var kept = throwaway.DataSources
                .SelectMany(ds => ds.Endpoints)
                .Where(e => e is not RouteEndpoint re
                    || !dropped.Any(d => re.RoutePattern.RawText!.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            endpoints.DataSources.Add(new FixedEndpointDataSource(kept));
        });

        var patterns = PatternsOf(host);
        Dump("probe2-filtered", patterns);

        patterns.Should().NotContain("/spark/auth/register");
        patterns.Should().NotContain("/spark/auth/login");
        patterns.Should().NotContain("/spark/auth/forgotPassword");
        patterns.Should().NotContain("/spark/auth/resetPassword");
        patterns.Should().NotContain("/spark/auth/resendConfirmationEmail");

        // The retained half must survive, or the mechanism is useless.
        patterns.Should().Contain(p => p.EndsWith("/manage/2fa", StringComparison.OrdinalIgnoreCase));
        patterns.Should().Contain(p => p.EndsWith("/manage/info", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Probe_3_conventions_applied_before_materialization_survive_republication()
    {
        // The antiforgery stamping at SparkAuthenticationExtensions.cs:84-101 runs as a
        // convention. If re-publication loses it, candidate (a) silently drops a CSRF
        // control — which would be a far worse outcome than the mapper being unfilterable.
        using var host = await StartAsync(endpoints =>
        {
            var throwaway = new ThrowawayEndpointRouteBuilder(endpoints.ServiceProvider);
            var convention = throwaway.MapGroup("/spark/auth").MapIdentityApi<SparkUser>();
            convention.Add(builder =>
            {
                if (builder is RouteEndpointBuilder route
                    && route.RoutePattern.RawText!.EndsWith("/manage/info", StringComparison.OrdinalIgnoreCase))
                {
                    route.Metadata.Add(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute(true));
                }
            });

            endpoints.DataSources.Add(new FixedEndpointDataSource([.. throwaway.DataSources.SelectMany(ds => ds.Endpoints)]));
        });

        var manageInfo = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.EndsWith("/manage/info", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        manageInfo.Should().NotBeEmpty();
        manageInfo.Should().Contain(e =>
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata>() != null);
    }
}
