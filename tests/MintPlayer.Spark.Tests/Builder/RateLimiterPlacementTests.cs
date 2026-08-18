using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.PersistentObject;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Where the rate limiter sits in <c>UseSpark()</c>'s pipeline (issue #265, F3).
/// <para>
/// It used to be the last thing <c>UseSpark</c> added, behind <c>UseAuthentication</c>. For an app
/// whose flood risk is an authenticated ingest endpoint — the reporting case authenticates a token
/// with a database lookup — that is the wrong side: the limiter only protected the app from load it
/// had already paid the expensive part of.
/// </para>
/// <para>
/// The assertion is deliberately behavioural rather than structural. Checking which stage the
/// middleware was registered into would restate the implementation; counting how many times the
/// authentication handler ran proves the property an adopter actually cares about — that a rejected
/// request costs no credential validation.
/// </para>
/// </summary>
public class RateLimiterPlacementTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("66666666-eeee-eeee-eeee-666666666666");

    private const string CountingScheme = "Counting";

    /// <summary>
    /// Stands in for an expensive credential check. It counts every invocation, so the test can ask
    /// whether authentication ran for a request the limiter rejected.
    /// </summary>
    private sealed class CountingHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public static int Invocations;

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Interlocked.Increment(ref Invocations);

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "machine")], CountingScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), CountingScheme)));
        }
    }

    [Fact]
    public async Task A_rate_limited_request_is_rejected_before_authentication_runs()
    {
        CountingHandler.Invocations = 0;

        // PermitLimit 1 over a long window: the first request is admitted, everything after it in
        // this test is rejected, with no chance of the window rolling over mid-test.
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store,
            [TestModels.Person(PersonTypeId)],
            configureSpark: spark => spark
                .AddCredentialScheme<AuthenticationSchemeOptions, CountingHandler>(CountingScheme)
                .AddRateLimiter(options =>
                {
                    options.PermitLimit = 1;
                    options.Window = TimeSpan.FromMinutes(5);
                }));

        using var client = factory.CreateClient();

        var admitted = await client.GetAsync($"/spark/po/{PersonTypeId}");
        admitted.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "the first request spends the only permit");

        var authAfterAdmitted = CountingHandler.Invocations;
        authAfterAdmitted.Should().BeGreaterThan(0,
            "the admitted request must reach authentication, or this test proves nothing about the "
            + "rejected one");

        var rejected = await client.GetAsync($"/spark/po/{PersonTypeId}");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        CountingHandler.Invocations.Should().Be(authAfterAdmitted,
            "a 429 must cost no credential validation — the limiter runs ahead of UseAuthentication");
    }

    [Fact]
    public async Task Routing_after_UseSpark_is_detected_and_then_refused()
    {
        // The ordering guard, end to end. Observed at request time via public API — an endpoint absent
        // when the BeforeAuthentication position runs and present once the request returns proves
        // routing sits downstream, with no dependence on ASP.NET's private property keys and no
        // guessing about hosting model.
        //
        // First offending request logs and arms; the next throws before doing any work, so the failure
        // lands on a request whose response has not started.
        using var host = await BuildMisorderedHostAsync();
        using var client = host.GetTestClient();

        var first = await client.GetAsync("/probe");
        first.IsSuccessStatusCode.Should().BeTrue(
            "the first offending request is allowed to complete — it is the one that detects the fault");

        var act = async () => await client.GetAsync("/probe");
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*UseRouting*");

        await host.StopAsync();
    }

    [Fact]
    public async Task An_unmatched_path_is_not_mistaken_for_misordering()
    {
        // The check must never fire on a 404: no endpoint either side of next(), which says nothing
        // about ordering. Without this the guard would break any misordered-looking app that simply
        // received a request for a path it does not serve.
        using var host = await BuildMisorderedHostAsync();
        using var client = host.GetTestClient();

        var missing = await client.GetAsync("/no-such-path");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Still unarmed — a second unmatched request must also pass rather than throw.
        var second = await client.GetAsync("/no-such-path");
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await host.StopAsync();
    }

    [Fact]
    public async Task Correctly_ordered_routing_never_arms_the_guard()
    {
        using var host = await BuildHostAsync(routingFirst: true);
        using var client = host.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/probe");
            response.IsSuccessStatusCode.Should().BeTrue();
        }

        await host.StopAsync();
    }

    [Fact]
    public async Task Minimal_hosting_without_an_explicit_UseRouting_is_not_refused()
    {
        // Regression for the false positive found in review, now structural rather than special-cased.
        // A minimal-hosting app never calls UseRouting(); WebApplication inserts routing at the FRONT
        // of the pipeline while it is built, so the ordering is correct even though nothing at
        // UseSpark-time could have said so. The request-time check sees an endpoint already selected
        // and settles — no marker key, no hosting-model branch.
        var webBuilder = WebApplication.CreateBuilder();
        webBuilder.Logging.ClearProviders();
        webBuilder.WebHost.UseTestServer();
        webBuilder.Services.AddSpark(spark => spark.AddRateLimiter());

        await using var app = webBuilder.Build();
        var appBuilder = (IApplicationBuilder)app;

        // No app.UseRouting() anywhere.
        UseRoutingOrderGuardOnly(appBuilder);
        app.MapGet("/probe", () => "ok");

        await app.StartAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/probe");
            response.IsSuccessStatusCode.Should().BeTrue(
                "minimal hosting routes ahead of this position, so the guard must never arm");
        }

        await app.StopAsync();
    }

    private static Task<IHost> BuildMisorderedHostAsync() => BuildHostAsync(routingFirst: false);

    /// <summary>
    /// A host carrying the ordering guard and one mapped endpoint, with routing either side of it.
    /// <para>
    /// Uses <c>UseSpark</c>'s guard in isolation rather than the whole of <c>UseSpark</c>: the full
    /// call needs a document store and a model hash, and neither has anything to do with ordering.
    /// The guard is reached through <see cref="SparkExtensions.UseSpark(IApplicationBuilder)"/> in
    /// production, and that wiring is covered by
    /// <see cref="Minimal_hosting_without_an_explicit_UseRouting_is_not_refused"/>.
    /// </para>
    /// </summary>
    private static async Task<IHost> BuildHostAsync(bool routingFirst)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging(l => l.ClearProviders());
                });
                web.Configure(app =>
                {
                    if (routingFirst) app.UseRouting();

                    UseRoutingOrderGuardOnly(app);

                    if (!routingFirst) app.UseRouting();

                    app.UseEndpoints(endpoints => endpoints.MapGet("/probe", () => "ok"));
                });
            })
            .StartAsync();

        return host;
    }

    /// <summary>
    /// Invokes <c>UseSpark</c>'s private ordering guard. Reflection is the lesser evil here: making it
    /// public would put a diagnostic on the framework's API surface purely so a test could reach it,
    /// and testing a copy of the logic would assert the copy.
    /// </summary>
    private static void UseRoutingOrderGuardOnly(IApplicationBuilder app)
    {
        var method = typeof(SparkExtensions).GetMethod(
            "UseRoutingOrderGuard",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "SparkExtensions.UseRoutingOrderGuard is gone or renamed — the ordering guard these "
                + "tests cover no longer exists under that name.");

        method.Invoke(null, [app]);
    }

    [Fact]
    public async Task An_unmetered_path_still_reaches_authentication()
    {
        // The mirror of the above: moving the limiter earlier must not turn it into a blanket gate.
        // A path outside PathPrefixes has to pass through untouched.
        CountingHandler.Invocations = 0;

        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store,
            [TestModels.Person(PersonTypeId)],
            configureSpark: spark => spark
                .AddCredentialScheme<AuthenticationSchemeOptions, CountingHandler>(CountingScheme)
                .AddRateLimiter(options =>
                {
                    options.PathPrefixes = ["/nowhere"];
                    options.PermitLimit = 1;
                    options.Window = TimeSpan.FromMinutes(5);
                }));

        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync($"/spark/po/{PersonTypeId}");
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                "/spark is not in PathPrefixes for this host, so it must not be metered");
        }

        CountingHandler.Invocations.Should().BeGreaterThanOrEqualTo(3);
    }
}
