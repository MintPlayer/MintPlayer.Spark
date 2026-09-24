using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using System.Security.Claims;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// Does WHERE the <c>XSRF-TOKEN</c> cookie is minted decide whether a blank refresh request is
/// needed after a principal change?
/// </summary>
/// <remarks>
/// <para>
/// An antiforgery token is bound to the caller's identity, so a token minted while anonymous is
/// refused for an authenticated request and vice versa. Spark mints the cookie <em>before</em> the
/// handler runs; <c>MintPlayer.AspNetCore.SpaServices.Xsrf</c> mints it in
/// <c>Response.OnStarting</c>, i.e. after. If the later placement sees the post-sign-in principal,
/// the client would not need to call <c>/spark/auth/csrf-refresh</c> afterwards.
/// </para>
/// <para>
/// ⚠️ This is an A/B, not an assertion of a belief. Both placements are driven through a real
/// Identity sign-in and sign-out, and the test records what each one actually produces. The answer
/// decides whether a published library changes, so it is measured rather than reasoned.
/// </para>
/// </remarks>
public class XsrfMintingPlacementTests : SparkTestDriver
{
    private const string CookieName = "XSRF-TOKEN";

    /// <summary>Public only because xUnit's <c>[InlineData]</c> needs to name it.</summary>
    public enum Placement { BeforeHandler, OnStarting }

    private async Task<IHost> StartAsync(Placement placement)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN");
                    services.AddAuthorization();
                    services.AddRouting();

                    // Spark's gate, not ASP.NET Core's UseAntiforgery() alone.
                    //
                    // ⚠️ The built-in middleware validates a JSON POST perfectly well — the method is
                    // what it gates on, not the content type — but it does NOT reject. On failure it
                    // records a verdict on IAntiforgeryValidationFeature and calls the next delegate
                    // anyway (AntiforgeryMiddleware.InvokeAwaited); rejecting is left to whatever
                    // reads that feature downstream. A hand-mapped endpoint has nobody doing it, so
                    // an earlier version of this test served 200 for a garbage token and looked
                    // green. Spark's middleware short-circuits with a 400, which is the behaviour
                    // being measured here.
                    services.AddSingleton(new SparkAntiforgeryOptions());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();

                    // The candidate under test. Everything else in the pipeline is identical.
                    app.Use(async (context, next) =>
                    {
                        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();

                        if (placement == Placement.OnStarting)
                        {
                            context.Response.OnStarting(state =>
                            {
                                var ctx = (HttpContext)state;
                                Append(ctx, antiforgery.GetAndStoreTokens(ctx).RequestToken);
                                return Task.CompletedTask;
                            }, context);
                            await next(context);
                            return;
                        }

                        Append(context, antiforgery.GetAndStoreTokens(context).RequestToken);
                        await next(context);
                    });

                    app.UseSparkAntiforgery();

                    // Both, exactly as SparkMiddleware does (:301 then :306). ASP.NET Core throws if
                    // an endpoint carries antiforgery metadata and no built-in middleware is present,
                    // even when Spark's gate already handled it.
                    app.UseAntiforgery();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/probe/sign-in", async (HttpContext ctx, SignInManager<SparkUser> signInManager, UserManager<SparkUser> userManager) =>
                        {
                            var user = await userManager.FindByNameAsync("probe@example.com");
                            await signInManager.SignInAsync(user!, isPersistent: false);
                            return Results.Ok(new { principalOnThisRequest = ctx.User.Identity?.Name });
                        });

                        endpoints.MapPost("/probe/sign-out", async (HttpContext ctx, SignInManager<SparkUser> signInManager) =>
                        {
                            await signInManager.SignOutAsync();
                            return Results.Ok(new { principalOnThisRequest = ctx.User.Identity?.Name });
                        });

                        // Anonymous but antiforgery-gated, so it can be called both signed in and
                        // signed out — which is what makes the sign-out half measurable.
                        endpoints.MapPost("/probe/protected", (HttpContext ctx) =>
                            Results.Ok(new { caller = ctx.User.Identity?.Name ?? "(anonymous)" }))
                            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
                    });
                }))
            .StartAsync();

        static void Append(HttpContext ctx, string? token)
        {
            if (token is null) return;
            ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = false,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps,
                Path = "/",
            });
        }
    }

    /// <summary>Everything a browser would be holding after a response.</summary>
    private sealed record Jar(Dictionary<string, string> Cookies)
    {
        public static Jar Empty() => new([]);

        public void Absorb(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
            foreach (var raw in values)
            {
                var pair = raw.Split(';')[0];
                var index = pair.IndexOf('=');
                if (index <= 0) continue;
                Cookies[pair[..index]] = pair[(index + 1)..];
            }
        }

        public string Header => string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}"));
        public string? Xsrf => Cookies.TryGetValue(CookieName, out var v) ? v : null;
    }

    private static async Task<HttpResponseMessage> PostAsync(IHost host, string path, Jar jar, bool sendXsrfHeader)
    {
        using var client = host.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (jar.Cookies.Count > 0) request.Headers.Add("Cookie", jar.Header);
        if (sendXsrfHeader && jar.Xsrf is { } token) request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);
        jar.Absorb(response);
        return response;
    }

    private async Task SeedUserAsync()
    {
        using var store = new UserStore<SparkUser>(Store);
        var existing = await store.FindByNameAsync("PROBE@EXAMPLE.COM", CancellationToken.None);
        if (existing is not null) return;

        var user = new SparkUser
        {
            UserName = "probe@example.com",
            NormalizedUserName = "PROBE@EXAMPLE.COM",
            Email = "probe@example.com",
            NormalizedEmail = "PROBE@EXAMPLE.COM",
            // Identity refuses to build a principal without one ("User security stamp cannot be
            // null"), and the claims factory needs a principal before any sign-in can happen.
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        (await store.CreateAsync(user, CancellationToken.None)).Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// The sign-in half: take the cookie minted on the sign-in response itself and use it
    /// immediately, with no refresh in between.
    /// </summary>
    [Theory]
    [InlineData(Placement.BeforeHandler)]
    [InlineData(Placement.OnStarting)]
    public async Task Token_minted_on_the_sign_in_response_is_usable_immediately(Placement placement)
    {
        await SeedUserAsync();
        using var host = await StartAsync(placement);
        var jar = Jar.Empty();

        var signIn = await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);

        var protectedCall = await PostAsync(host, "/probe/protected", jar, sendXsrfHeader: true);

        var expected = placement switch
        {
            // Minted before the handler ran, i.e. while still anonymous. The follow-up call is
            // authenticated, so the token is "meant for a different claims-based user".
            Placement.BeforeHandler => HttpStatusCode.BadRequest,

            // SignInManager assigns Context.User on the current request, so a token minted at
            // response-start is already bound to the signed-in principal.
            Placement.OnStarting => HttpStatusCode.OK,

            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };

        protectedCall.StatusCode.Should().Be(expected,
            "placement decides which principal the sign-in response's token is bound to");
    }

    /// <summary>
    /// The sign-out half — the one the reasoning says cannot be fixed by placement, because
    /// <c>SignOutAsync</c> never resets <c>HttpContext.User</c>.
    /// </summary>
    [Theory]
    [InlineData(Placement.BeforeHandler)]
    [InlineData(Placement.OnStarting)]
    public async Task Token_minted_on_the_sign_out_response_is_usable_immediately(Placement placement)
    {
        await SeedUserAsync();
        using var host = await StartAsync(placement);
        var jar = Jar.Empty();

        await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);

        var signOut = await PostAsync(host, "/probe/sign-out", jar, sendXsrfHeader: false);
        signOut.StatusCode.Should().Be(HttpStatusCode.OK);

        // The auth cookie is gone now, so this call is anonymous. If the XSRF token minted on the
        // sign-out response is still bound to the departed user, it is refused.
        var protectedCall = await PostAsync(host, "/probe/protected", jar, sendXsrfHeader: true);

        // ⚠️ THE ANSWER: both placements fail here, and that is the point. SignOutAsync never resets
        // HttpContext.User, so at response-start the principal is still the user who just logged
        // out — the token is bound to them, and the next (anonymous) request is refused. No choice
        // of placement fixes this; only a fresh request, where authentication re-evaluates from the
        // cookies that no longer exist, can mint an anonymous-bound token.
        protectedCall.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "sign-out cannot be fixed by moving where the cookie is minted");
    }

    /// <summary>
    /// ⚠️ THE CONTROL. Everything else in this file is worthless without it: if the gate does not
    /// actually reject a bad token, every "it worked" above is the pipeline waving requests through,
    /// not evidence about minting placement.
    /// </summary>
    [Theory]
    [InlineData(Placement.BeforeHandler)]
    [InlineData(Placement.OnStarting)]
    public async Task A_garbage_token_is_rejected(Placement placement)
    {
        await SeedUserAsync();
        using var host = await StartAsync(placement);
        var jar = Jar.Empty();

        await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);

        using var client = host.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/probe/protected");
        request.Headers.Add("Cookie", jar.Header);
        request.Headers.Add("X-XSRF-TOKEN", "obviously-not-a-valid-token");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "if this passes, the gate is not validating and no other result in this file means anything");
    }

    /// <summary>⚠️ The second control: no token at all must also be refused.</summary>
    [Fact]
    public async Task A_missing_token_is_rejected()
    {
        await SeedUserAsync();
        using var host = await StartAsync(Placement.OnStarting);
        var jar = Jar.Empty();

        await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);

        (await PostAsync(host, "/probe/protected", jar, sendXsrfHeader: false)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The mechanism behind both results, asserted directly so the reason is recorded rather than
    /// inferred: does the current request's principal reflect the sign-in / sign-out that just ran?
    /// </summary>
    [Fact]
    public async Task Sign_in_updates_the_current_principal_and_sign_out_does_not()
    {
        await SeedUserAsync();
        using var host = await StartAsync(Placement.OnStarting);
        var jar = Jar.Empty();

        var signIn = await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);
        (await signIn.Content.ReadAsStringAsync())
            .Should().Contain("probe@example.com",
                "SignInManager assigns Context.User, so the sign-in is visible on its own request");

        var signOut = await PostAsync(host, "/probe/sign-out", jar, sendXsrfHeader: false);
        (await signOut.Content.ReadAsStringAsync())
            .Should().Contain("probe@example.com",
                "SignOutAsync does NOT reset Context.User — the departed identity is still the "
                + "principal on this request, which is why placement alone cannot fix sign-out");
    }
}
