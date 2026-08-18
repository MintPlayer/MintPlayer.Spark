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
    public void UseSpark_before_UseRouting_is_refused_when_something_is_registered_early()
    {
        // BeforeAuthentication's contract is that routing has already run. UseSpark is documented as
        // "call after UseRouting()", but documentation is not enforcement: get the order wrong and the
        // limiter sits ahead of endpoint selection, where [EnableRateLimiting] / [DisableRateLimiting]
        // silently stop applying and metering falls back to global-only. That is the same
        // quietly-doing-less failure this whole change exists to remove, so it is checked.
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddLogging();
        services.AddSpark(spark => spark.AddRateLimiter());
        using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);

        // No app.UseRouting() — the mistake under test.
        var act = () => app.UseSpark();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*UseRouting*");
    }

    [Fact]
    public void UseSpark_before_UseRouting_is_tolerated_when_nothing_is_registered_early()
    {
        // The check must not become a blanket new requirement. An app with no BeforeAuthentication
        // middleware cannot be affected by the ordering, so failing it would cost churn for nothing.
        //
        // Asserts only that the routing guard does not fire; UseSpark goes on to do other work
        // (index creation, model-hash verification) that needs a full host, so anything it throws
        // afterwards is out of scope here.
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddLogging();
        services.AddSpark(spark => { });
        using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);

        try
        {
            app.UseSpark();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("UseRouting"))
        {
            Assert.Fail("the routing guard fired even though nothing was registered early");
        }
        catch
        {
            // Any other failure is later machinery, not the guard.
        }
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
