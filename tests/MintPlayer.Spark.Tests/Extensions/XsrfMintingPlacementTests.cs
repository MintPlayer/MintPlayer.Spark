using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// Is the <c>XSRF-TOKEN</c> minted on a sign-in or sign-out response usable for the very next
/// mutating call, with no <c>/spark/auth/csrf-refresh</c> in between?
/// </summary>
/// <remarks>
/// <para>
/// An antiforgery token is bound to the caller's identity, so a token minted while anonymous is
/// refused for an authenticated request and vice versa. Which principal the token on a sign-in or
/// sign-out response is bound to therefore depends on <em>when</em> it is minted.
/// </para>
/// <para>
/// History: this file began as an A/B between Spark's own mint, which ran <em>before</em> the
/// handler, and <c>MintPlayer.AspNetCore.SpaServices.Xsrf</c>, which mints in
/// <c>Response.OnStarting</c>. Measured then: after sign-in, 400 for the eager mint and 200 for
/// <c>OnStarting</c>; after sign-out, 400 for both. That result is why Spark adopted the package
/// (#452). The eager arm is gone, and the tests now drive the package's real middleware, so a
/// change to the package's placement fails here and not just in its own repository.
/// </para>
/// </remarks>
public class XsrfMintingPlacementTests : SparkTestDriver
{
    private const string CookieName = "XSRF-TOKEN";

    private async Task<IHost> StartAsync()
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

                    // The same order as SparkMiddleware: Spark's gate, then the built-in middleware
                    // (ASP.NET Core throws if an endpoint carries antiforgery metadata and it is
                    // absent, even when Spark's gate already handled it), then the mint.
                    app.UseSparkAntiforgery();
                    app.UseAntiforgery();
                    app.UseAntiforgeryGenerator();

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
                            .WithMetadata(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute(true));
                    });
                }))
            .StartAsync();
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

    /// <summary>
    /// Seeds the probe user and — the part that is not optional — waits until a query can see it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>UserManager.FindByNameAsync</c> resolves through a query, so the document being written
    /// is not the same thing as the document being findable. Without the wait this races: the store
    /// write returns, the host starts, the sign-in endpoint looks the user up against a still-stale
    /// index, gets <see langword="null"/>, and <c>SignInWithClaimsAsync</c> throws
    /// <c>ArgumentNullException (Parameter 'user')</c> from deep inside Identity — a stack that
    /// names neither this method nor indexing and reads like an Identity bug.
    /// <para>
    /// It failed exactly once in ~2,500 tests, which is the worst frequency to leave alone: often
    /// enough to erode trust in a red run, rare enough to be dismissed as noise every time.
    /// </para>
    /// </remarks>
    private async Task SeedUserAsync()
    {
        using var store = new UserStore<SparkUser>(Store);
        var existing = await store.FindByNameAsync("PROBE@EXAMPLE.COM", CancellationToken.None);
        if (existing is not null)
        {
            await WaitForIndexesAsync();
            return;
        }

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
        await WaitForIndexesAsync();
    }

    /// <summary>
    /// The sign-in half: take the cookie minted on the sign-in response itself and use it
    /// immediately, with no refresh in between.
    /// </summary>
    [Fact]
    public async Task Token_minted_on_the_sign_in_response_is_usable_immediately()
    {
        await SeedUserAsync();
        using var host = await StartAsync();
        var jar = Jar.Empty();

        var signIn = await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);

        var protectedCall = await PostAsync(host, "/probe/protected", jar, sendXsrfHeader: true);

        // SignInManager assigns Context.User on the current request, and the package mints at
        // response-start, so the token is already bound to the signed-in principal. Spark's old
        // eager mint got 400 here: it minted while the request was still anonymous.
        protectedCall.StatusCode.Should().Be(HttpStatusCode.OK,
            "the package mints after the handler, so the sign-in response's token is bound to the signed-in user");
    }

    /// <summary>
    /// The sign-out half, which placement cannot fix, because <c>SignOutAsync</c> never resets
    /// <c>HttpContext.User</c>. This is why <c>/spark/auth/csrf-refresh</c> stays.
    /// </summary>
    [Fact]
    public async Task Token_minted_on_the_sign_out_response_is_not_usable_immediately()
    {
        await SeedUserAsync();
        using var host = await StartAsync();
        var jar = Jar.Empty();

        await PostAsync(host, "/probe/sign-in", jar, sendXsrfHeader: false);

        var signOut = await PostAsync(host, "/probe/sign-out", jar, sendXsrfHeader: false);
        signOut.StatusCode.Should().Be(HttpStatusCode.OK);

        // The auth cookie is gone now, so this call is anonymous. The XSRF token minted on the
        // sign-out response is still bound to the departed user, so it is refused. Only a fresh
        // request, where authentication re-evaluates from cookies that no longer exist, can mint
        // an anonymous-bound token.
        var protectedCall = await PostAsync(host, "/probe/protected", jar, sendXsrfHeader: true);

        protectedCall.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "sign-out cannot be fixed by moving where the cookie is minted");
    }

    /// <summary>
    /// ⚠️ THE CONTROL. Everything else in this file is worthless without it: if the gate does not
    /// actually reject a bad token, every "it worked" above is the pipeline waving requests through,
    /// not evidence about minting placement.
    /// </summary>
    [Fact]
    public async Task A_garbage_token_is_rejected()
    {
        await SeedUserAsync();
        using var host = await StartAsync();
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
        using var host = await StartAsync();
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
        using var host = await StartAsync();
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
