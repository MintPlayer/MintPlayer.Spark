using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// #300 — Spark's antiforgery gate fired only on endpoints carrying <c>IAntiforgeryMetadata</c>.
/// Nothing attaches that by default, and MVC's own <c>[ValidateAntiForgeryToken]</c> implements a
/// different interface entirely, so an app's cookie-authenticated writes were unprotected and the
/// obviously-correct annotation did not change that.
/// <para>
/// These tests exercise the gate directly rather than through <c>UseSpark()</c>, which would drag in
/// RavenDB, the model-hash check and index creation for a decision that is a header comparison. The
/// gate is the whole of the behaviour under test; <c>UseSpark()</c> only positions it.
/// </para>
/// </summary>
public class SparkAntiforgeryTests
{
    private const string AmbientScheme = "TestCookie";
    private const string BearerScheme = "TestBearer";

    private sealed class SchemeFeature(SparkCredentialScheme scheme) : ISparkAuthenticatedSchemeFeature
    {
        public SparkCredentialScheme Scheme { get; } = scheme;
    }

    /// <summary>
    /// Builds a host whose pipeline is routing → a stand-in for authentication → the gate → endpoints.
    /// <para>
    /// The stand-in reads an <c>X-Test-Auth</c> header and sets both <c>HttpContext.User</c> and the
    /// scheme feature exactly as <c>SparkCompositeAuthenticationHandler</c> would. Using a real
    /// handler would test ASP.NET Core's authentication rather than Spark's decision, and the gate
    /// reads only these two things.
    /// </para>
    /// </summary>
    private static async Task<IHost> StartAsync(Action<SparkAntiforgeryOptions> configure)
    {
        var options = new SparkAntiforgeryOptions();
        configure(options);

        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(options);
                    services.AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN");
                    services.AddRouting();
                    services.AddControllers().AddApplicationPart(typeof(SparkAntiforgeryTests).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();

                    app.Use(async (context, next) =>
                    {
                        var auth = context.Request.Headers["X-Test-Auth"].ToString();
                        if (auth is AmbientScheme or BearerScheme)
                        {
                            context.User = new ClaimsPrincipal(
                                new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], auth));
                            context.Features.Set<ISparkAuthenticatedSchemeFeature>(
                                new SchemeFeature(new SparkCredentialScheme(auth, IsAmbient: auth == AmbientScheme)));
                        }

                        await next(context);
                    });

                    app.UseSparkAntiforgery();
                    app.UseAntiforgery();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();

                        // The discriminator against a metadata-stamping design: no MVC convention
                        // reaches a minimal-API handler the app wrote, so a stamping implementation
                        // covers controllers and leaves this silently open.
                        endpoints.MapPost("/api/minimal", () => Results.Ok("minimal"));

                        endpoints.MapPost("/outside/minimal", () => Results.Ok("outside"));

                        // Hands the caller a token pair the way Spark's own XSRF-TOKEN middleware
                        // does, so a test can play the part of a browser that read the cookie.
                        endpoints.MapGet("/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
                        {
                            var tokens = antiforgery.GetAndStoreTokens(context);
                            return Results.Text(tokens.RequestToken!);
                        });
                    });
                }))
            .StartAsync();
    }

    /// <summary>
    /// The cookie/header pair a browser would hold. Taken from the running host rather than
    /// synthesized, because the pair is cryptographically bound and only the server can mint it.
    /// </summary>
    private static async Task<(string Cookie, string Header)> GetTokensAsync(HttpClient client)
    {
        // Fetched as the SAME user the POST will run as. An antiforgery token embeds the caller's
        // identity, so a token minted anonymously is rejected for an authenticated request with
        // "meant for a different claims-based user" — which reads exactly like a broken gate.
        var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/antiforgery/token");
        tokenRequest.Headers.Add("X-Test-Auth", AmbientScheme);
        var response = await client.SendAsync(tokenRequest);
        response.EnsureSuccessStatusCode();

        var header = await response.Content.ReadAsStringAsync();
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));

        return (setCookie.Split(';')[0], header);
    }

    private static HttpRequestMessage Post(string path, string? auth = AmbientScheme)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (auth is not null)
            request.Headers.Add("X-Test-Auth", auth);
        return request;
    }

    [Fact]
    public async Task A_cookie_authenticated_post_without_a_token_is_rejected()
    {
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/mint"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_cookie_authenticated_post_with_a_token_succeeds()
    {
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();
        var (cookie, header) = await GetTokensAsync(client);

        var request = Post("/api/tokens/mint");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", header);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bearer_authenticated_post_is_unaffected()
    {
        // CSRF is an attack on ambient authority. A caller that had to construct its own
        // Authorization header cannot be made to do so by a third-party page, and has no XSRF-TOKEN
        // cookie to echo — demanding one would simply make external POSTs impossible.
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/mint", BearerScheme));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_anonymous_post_is_unaffected()
    {
        // A request with no credential is neither ambient nor non-ambient. There is no authority to
        // ride, so a check here protects nothing while breaking every non-browser caller of a public
        // endpoint — a webhook receiver, a contact form.
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/mint", auth: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_minimal_api_post_in_scope_is_covered()
    {
        // THE discriminating check for D3. No MVC convention and no endpoint convention attached to
        // MapControllers() reaches a MapPost the app wrote, so any metadata-stamping design passes
        // every other test here and fails this one.
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/minimal"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DisableAntiforgery_still_exempts()
    {
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/exempt"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "explicit metadata wins over the default");
    }

    [Fact]
    public async Task A_path_outside_the_prefixes_is_unaffected()
    {
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/outside/minimal"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_default_is_off_so_an_unannotated_endpoint_keeps_working()
    {
        // RequireAntiforgery ships off this preview: turning it on rejects writes from any client
        // that does not echo the cookie, and an app upgrading Spark should learn that from a release
        // note rather than from production 400s.
        using var host = await StartAsync(_ => { });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/mint"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_explicit_RequireAntiforgeryToken_applies_even_with_the_default_off()
    {
        // The pre-existing one-line workaround, which keeps working unchanged. Worth pinning: it is
        // what an app on an older Spark was told to do, and the inverted default must not quietly
        // take it over.
        using var host = await StartAsync(_ => { });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/always"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_explicitly_required_endpoint_is_checked_even_for_an_anonymous_caller()
    {
        // The ambient test belongs only to the default branch. /spark/auth/login is exactly this
        // shape — anonymous and explicitly protected — and login CSRF is a real attack.
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/always", auth: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Warning_only_mode_logs_and_allows()
    {
        using var host = await StartAsync(o =>
        {
            o.RequireAntiforgery = true;
            o.WarnOnly = true;
            o.PathPrefixes = ["/api"];
        });
        var client = host.GetTestClient();

        var response = await client.SendAsync(Post("/api/tokens/mint"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "warning-only is the migration path onto the flip");
    }
}

/// <summary>
/// A controller mapped the ordinary way. It carries no antiforgery annotation, which is the
/// point: the requirement is that an app gets protection <em>without</em> per-endpoint opt-in.
/// </summary>
[ApiController]
[Route("api/tokens")]
public sealed class SparkAntiforgeryTokensController : ControllerBase
{
    [HttpPost("mint")]
    public IActionResult Mint() => Ok("minted");

    // The framework's own public attribute, and the only one that implements the interface
    // Spark's gate reads. MVC's [IgnoreAntiforgeryToken] / [ValidateAntiForgeryToken] implement
    // IAntiforgeryPolicy instead, which is exactly why the obviously-correct annotation did
    // nothing (F8).
    [HttpPost("exempt")]
    [RequireAntiforgeryToken(required: false)]
    public IActionResult Exempt() => Ok("exempt");

    [HttpPost("always")]
    [RequireAntiforgeryToken]
    public IActionResult Always() => Ok("always");
}
