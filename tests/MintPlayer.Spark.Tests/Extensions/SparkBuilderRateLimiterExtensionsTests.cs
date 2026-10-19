using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
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
    /// Every way of writing "no prefixes at all": absent, blank, whitespace. A bare <c>"/"</c> is
    /// deliberately NOT in this set — it is a different mistake and gets its own message.
    /// </summary>
    public static TheoryData<string[]> EmptyPrefixCases()
    {
        var cases = new TheoryData<string[]>();
        cases.Add([]);
        cases.Add([""]);
        cases.Add(["   "]);
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
           .WithMessage("*PathPrefixes*")
           .And.Which.ParamName.Should().Be(nameof(SparkRateLimiterOptions.PathPrefixes),
               "the caller set PathPrefixes, so naming an internal parameter tells them nothing");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("  /  ")]
    public void A_bare_root_prefix_is_refused_on_its_own_terms(string root)
    {
        // Previously this normalized to empty and was reported as "you named no prefixes" — wrong for
        // someone who named exactly one. The refusal stands (it would meter static assets too), but it
        // has to explain itself, or a caller who wrote "/" deliberately is told they configured nothing.
        var builder = new TestBuilder();

        var act = () => builder.AddRateLimiter(options => options.PathPrefixes = [root]);

        var message = act.Should().Throw<ArgumentException>().Which.Message;
        message.Should().Contain("every request",
            "the reason is that it meters everything, not that it is missing");
        message.Should().NotContain("at least one path prefix",
            "that is the empty-configuration message and it does not apply here");
    }

    [Fact]
    public void A_root_alongside_a_real_prefix_is_ignored_rather_than_fatal()
    {
        // "/" contributes nothing to the scope, so with a usable prefix present there is no ambiguity
        // to refuse — the configuration expresses a coherent intent and is honoured.
        var builder = new TestBuilder();

        var act = () => builder.AddRateLimiter(options => options.PathPrefixes = ["/", "/api"]);

        act.Should().NotThrow();
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
                web.Configure(app =>
                {
                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.BeforeAuthentication);
                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);
                });
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
                    // Both stages, in the order UseSpark applies them. The limiter lives in
                    // BeforeAuthentication, so applying only AfterSpark would wire an empty
                    // pipeline and every assertion here would pass for the wrong reason.
                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.BeforeAuthentication);
                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);
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

    // ── Who a caller is (commerce PRD X10, F5) ───────────────────────────────────────────────────

    /// <summary>One request of a burst: from an address, optionally as a signed-in user or with a visitor cookie.</summary>
    private sealed record Caller(string Ip, string? User = null, string? VisitorId = null, string? Authorization = null);

    /// <summary>
    /// Boots the limiter between a fake remote address (ahead of it) and a fake authentication (between its two
    /// stages, where <c>UseAuthentication</c> sits in <c>UseSpark()</c>), and returns each request's status.
    /// </summary>
    private static async Task<List<HttpStatusCode>> MeasureCallersAsync(Action<SparkRateLimiterOptions> configure, params Caller[] callers)
    {
        var builder = new TestBuilder();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
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
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Ip"].ToString());
                        return next(context);
                    });
                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.BeforeAuthentication);
                    app.Use((context, next) =>
                    {
                        if (context.Request.Headers["X-Test-User"].ToString() is { Length: > 0 } user)
                            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "Test"));
                        return next(context);
                    });
                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);
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
            var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MintPlayer.Spark.Visitor.SparkVisitorFeature.Purpose);
            var client = host.GetTestClient();
            var codes = new List<HttpStatusCode>();
            foreach (var caller in callers)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/spark/anything");
                request.Headers.Add("X-Test-Ip", caller.Ip);
                if (caller.User is not null)
                {
                    request.Headers.Add("X-Test-User", caller.User);
                    // What a real signed-in request carries: a credential the limiter cannot read before authentication.
                    request.Headers.TryAddWithoutValidation("Authorization", "Test " + caller.User);
                }
                if (caller.Authorization is not null)
                    request.Headers.TryAddWithoutValidation("Authorization", caller.Authorization);
                if (caller.VisitorId is not null)
                    request.Headers.Add("Cookie", ".Spark.Visitor=" + protector.Protect(
                        "{\"v\":\"" + caller.VisitorId + "\",\"iat\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}"));
                using var response = await client.SendAsync(request);
                codes.Add(response.StatusCode);
            }
            return codes;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task F5_addresses_in_one_IPv6_64_share_one_budget()
    {
        // Red before X10: the partition key was the exact address, so one host rotating its interface id
        // (RFC 8981 does that by itself) got a fresh budget per address.
        var codes = await MeasureCallersAsync(
            o => o.PermitLimit = 1,
            new Caller("2001:db8:0:1::1"),
            new Caller("2001:db8:0:1:ffff:eeee:dddd:cccc"),
            new Caller("2001:db8:0:2::1"));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signed_in_users_behind_one_address_each_have_their_own_budget()
    {
        // A carrier NAT: everyone shares one address, and used to share one budget of PermitLimit.
        var codes = await MeasureCallersAsync(
            o => o.PermitLimit = 1,
            new Caller("198.51.100.1", User: "ann"),
            new Caller("198.51.100.1", User: "bob"),
            new Caller("198.51.100.1", User: "ann"));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Visitors_behind_one_address_each_have_their_own_budget()
    {
        var one = Guid.NewGuid().ToString("N");
        var two = Guid.NewGuid().ToString("N");
        var codes = await MeasureCallersAsync(
            o => o.PermitLimit = 1,
            new Caller("198.51.100.1", VisitorId: one),
            new Caller("198.51.100.1", VisitorId: two),
            new Caller("198.51.100.1", VisitorId: one));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_address_ceiling_bounds_every_caller_behind_one_address_together()
    {
        var codes = await MeasureCallersAsync(
            o =>
            {
                o.PermitLimit = 1;
                o.PermitLimitPerAddress = 2;
            },
            new Caller("198.51.100.1", User: "ann"),
            new Caller("198.51.100.1", VisitorId: Guid.NewGuid().ToString("N")),
            new Caller("198.51.100.1", User: "bob"),
            new Caller("198.51.100.2", User: "carl"));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_credential_that_does_not_authenticate_is_metered_as_its_address()
    {
        // A junk Authorization header defers the caller budget to after authentication; there it turns out to be
        // nobody, and is keyed on its address like any anonymous caller — never a fresh budget per junk value.
        var codes = await MeasureCallersAsync(
            o => o.PermitLimit = 1,
            new Caller("198.51.100.1", Authorization: "Bearer junk-1"),
            new Caller("198.51.100.1", Authorization: "Bearer junk-2"));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void An_address_ceiling_below_the_caller_budget_is_refused()
    {
        var act = () => new TestBuilder().AddRateLimiter(o =>
        {
            o.PermitLimit = 10;
            o.PermitLimitPerAddress = 5;
        });

        act.Should().Throw<ArgumentException>().WithMessage("*PermitLimitPerAddress*");
    }

    // ── Configuration binding (Spark:RateLimiter) ────────────────────────────────────────────────

    /// <summary>
    /// The budget binds from <c>Spark:RateLimiter</c>, so an operator can retune it per environment
    /// without a redeploy — and so a test host can raise it without disabling the limiter.
    /// </summary>
    /// <remarks>
    /// ⚠️ This path exists because <c>issue_265_PRD.md</c> D5 deferred it, and D5's stated objection
    /// was "a second, untested path to the same setting". These three tests are the answer to that
    /// objection; deleting them puts the objection back.
    /// </remarks>
    [Fact]
    public void AddRateLimiter_binds_the_budget_from_configuration()
    {
        var captured = new SparkRateLimiterOptions();
        var builder = new TestBuilder(new Dictionary<string, string?>
        {
            ["Spark:RateLimiter:PermitLimit"] = "1000",
            ["Spark:RateLimiter:Window"] = "00:00:30",
        });

        builder.AddRateLimiter(captured.CopyFrom);

        captured.PermitLimit.Should().Be(1000);
        captured.Window.Should().Be(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Code wins over configuration, matching <c>AddReplication</c> and <c>AddMessaging</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ The consequence is that an app which assigns a value in its configurator cannot be
    /// overridden from appsettings, and the override fails <b>silently</b>. Fleet passes
    /// <c>_ => { }</c> precisely so the E2E budget survives; adding an assignment there would revert
    /// the suite to the default budget with nothing announcing it. Pinned here so that change breaks
    /// a test instead.
    /// </remarks>
    [Fact]
    public void A_code_configurator_wins_over_configuration()
    {
        var captured = new SparkRateLimiterOptions();
        var builder = new TestBuilder(new Dictionary<string, string?>
        {
            ["Spark:RateLimiter:PermitLimit"] = "1000",
        });

        builder.AddRateLimiter(options =>
        {
            options.PermitLimit = 7;
            captured.CopyFrom(options);
        });

        captured.PermitLimit.Should().Be(7, "the configurator runs after the binder");
    }

    /// <summary>
    /// A builder with no configuration at all still works — the parameterless <c>AddSpark</c>
    /// overload constructs one, so binding has to be null-tolerant.
    /// </summary>
    [Fact]
    public void Binding_is_skipped_when_the_builder_carries_no_configuration()
    {
        var captured = new SparkRateLimiterOptions();
        var builder = new TestBuilder();

        builder.AddRateLimiter(captured.CopyFrom);

        captured.PermitLimit.Should().Be(150, "the documented default survives a null configuration");
    }

    private sealed class TestBuilder : ISparkBuilder
    {
        public TestBuilder(IDictionary<string, string?>? settings = null)
            => Configuration = settings is null
                ? null
                : new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration { get; }
        public SparkModuleRegistry Registry { get; } = new();
    }
}

file static class SparkRateLimiterOptionsTestExtensions
{
    /// <summary>Copies the values under test out of the live options object.</summary>
    public static void CopyFrom(this SparkRateLimiterOptions target, SparkRateLimiterOptions source)
    {
        target.PermitLimit = source.PermitLimit;
        target.Window = source.Window;
        target.PathPrefixes = source.PathPrefixes;
    }
}
