using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Visitor;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Visitor;

/// <summary>
/// Visitor identity (commerce PRD X1, spike S16): the cookie is issued only by an action and only through
/// Data Protection; an anonymous mutating call that carries a valid one is held to antiforgery; and a guest is
/// merged into the account it signs in to exactly once, by the sign-in hook or by the next request.
/// </summary>
/// <remarks>
/// A host with exactly the <c>UseSpark()</c> order that matters here: authentication, <c>UseSparkVisitor</c>, the
/// XSRF mint, Spark's antiforgery gate. The probe endpoints sit under <c>/spark</c>, the gate's default prefix.
/// </remarks>
public class SparkVisitorTests : SparkTestDriver
{
    private const string VisitorCookie = ".Spark.Visitor";
    private const string XsrfCookie = "XSRF-TOKEN";

    /// <summary>Records every merge a handler ran; the document it wrote proves which ones were committed.</summary>
    private sealed class RecordingMergeHandler : ISparkVisitorMergeHandler
    {
        public ConcurrentBag<(string VisitorId, string UserId, string DocumentId)> Calls { get; } = [];

        public async Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken)
        {
            var basket = new MergedBasket { Id = "MergedBaskets/" + Guid.NewGuid().ToString("N"), VisitorId = visitorId, UserId = userId };
            await session.StoreAsync(basket, basket.Id, cancellationToken);
            Calls.Add((visitorId, userId, basket.Id));
        }
    }

    private sealed class MergedBasket
    {
        public string? Id { get; set; }
        public string VisitorId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
    }

    private sealed record WhoAmI(string? VisitorId, string? UserId, string IdentityKey);

    private readonly RecordingMergeHandler merges = new();

    private async Task<IHost> StartAsync()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN");
                    services.AddAuthorization();
                    services.AddRouting();
                    services.AddSingleton(new SparkAntiforgeryOptions());
                    services.AddSparkVisitor();
                    services.AddSingleton<ISparkVisitorMergeHandler>(merges);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseSparkVisitor();
                    app.UseAntiforgeryGenerator();
                    app.UseSparkAntiforgery();
                    app.UseAntiforgery();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/spark/vt/ensure", async (ISparkVisitor visitor) => Results.Ok(new { visitorId = await visitor.EnsureAsync() }));
                        endpoints.MapGet("/spark/vt/ensure-on-get", async (ISparkVisitor visitor) =>
                        {
                            try
                            {
                                await visitor.EnsureAsync();
                                return Results.Ok();
                            }
                            catch (InvalidOperationException)
                            {
                                return Results.Conflict();
                            }
                        });
                        endpoints.MapGet("/spark/vt/whoami", (ISparkVisitor visitor) => Results.Ok(new WhoAmI(visitor.VisitorId, visitor.UserId, visitor.IdentityKey)));
                        endpoints.MapPost("/spark/vt/echo", () => Results.Ok());
                        endpoints.MapPost("/probe/sign-in", async (SignInManager<SparkUser> signInManager, UserManager<SparkUser> userManager) =>
                        {
                            var user = await userManager.FindByNameAsync("visitor-probe@example.com");
                            await signInManager.SignInAsync(user!, isPersistent: false);
                            return Results.Ok(new { userId = user!.Id });
                        });
                    });
                }))
            .StartAsync();
    }

    private sealed record Jar(Dictionary<string, string> Cookies)
    {
        public static Jar Empty() => new([]);

        public List<string> LastSetCookies { get; } = [];

        public void Absorb(HttpResponseMessage response)
        {
            LastSetCookies.Clear();
            if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
            foreach (var raw in values)
            {
                LastSetCookies.Add(raw);
                var pair = raw.Split(';')[0];
                var index = pair.IndexOf('=');
                if (index <= 0) continue;
                var value = pair[(index + 1)..];
                // A deletion is an empty value with an expiry in the past.
                if (value.Length == 0) Cookies.Remove(pair[..index]);
                else Cookies[pair[..index]] = value;
            }
        }

        public string? Visitor => Cookies.TryGetValue(VisitorCookie, out var v) ? v : null;
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, HttpMethod method, string path, Jar jar, bool sendXsrfHeader = true)
    {
        using var client = host.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (jar.Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", jar.Cookies.Select(c => $"{c.Key}={c.Value}")));
        if (sendXsrfHeader && jar.Cookies.TryGetValue(XsrfCookie, out var token)) request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await client.SendAsync(request);
        jar.Absorb(response);
        return response;
    }

    private async Task<WhoAmI> WhoAmIAsync(IHost host, Jar jar)
        => (await (await SendAsync(host, HttpMethod.Get, "/spark/vt/whoami", jar)).Content.ReadFromJsonAsync<WhoAmI>())!;

    /// <summary>An anonymous browser: one GET for the XSRF token, then a POST that issues the visitor cookie.</summary>
    private async Task<(Jar Jar, string VisitorId)> GuestAsync(IHost host)
    {
        var jar = Jar.Empty();
        await SendAsync(host, HttpMethod.Get, "/spark/vt/whoami", jar);
        var ensure = await SendAsync(host, HttpMethod.Post, "/spark/vt/ensure", jar);
        ensure.StatusCode.Should().Be(HttpStatusCode.OK);
        var visitorId = (await ensure.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["visitorId"];
        return (jar, visitorId);
    }

    private async Task<string> SeedUserAsync()
    {
        using var store = new UserStore<SparkUser>(Store);
        if (await store.FindByNameAsync("VISITOR-PROBE@EXAMPLE.COM", CancellationToken.None) is { } existing)
        {
            await WaitForIndexesAsync();
            return existing.Id!;
        }

        var user = new SparkUser
        {
            UserName = "visitor-probe@example.com",
            NormalizedUserName = "VISITOR-PROBE@EXAMPLE.COM",
            Email = "visitor-probe@example.com",
            NormalizedEmail = "VISITOR-PROBE@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        (await store.CreateAsync(user, CancellationToken.None)).Succeeded.Should().BeTrue();
        await WaitForIndexesAsync();
        return user.Id!;
    }

    private async Task<int> CommittedMergesAsync()
    {
        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<MergedBasket>(merges.Calls.Select(c => c.DocumentId));
        return loaded.Values.Count(v => v is not null);
    }

    // ---- the cookie -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_issues_an_HttpOnly_Lax_cookie_that_identifies_the_visitor_on_the_next_request()
    {
        using var host = await StartAsync();
        var (jar, visitorId) = await GuestAsync(host);

        var setCookie = jar.LastSetCookies.Single(c => c.StartsWith(VisitorCookie + "=", StringComparison.Ordinal)).ToLowerInvariant();
        setCookie.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("path=/").And.Contain("expires=");
        jar.Visitor.Should().NotContain(visitorId, "the value is protected by Data Protection, not the id in clear");

        var me = await WhoAmIAsync(host, jar);
        me.VisitorId.Should().Be(visitorId);
        me.UserId.Should().BeNull("a visitor is not an authenticated identity");
        me.IdentityKey.Should().Be("visitor:" + visitorId);
    }

    [Fact]
    public async Task A_read_never_issues_the_cookie()
    {
        using var host = await StartAsync();
        var jar = Jar.Empty();

        var response = await SendAsync(host, HttpMethod.Get, "/spark/vt/ensure-on-get", jar);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "EnsureAsync refuses on GET: a strictly necessary cookie is set by an action");
        jar.Visitor.Should().BeNull();
    }

    [Fact]
    public async Task A_tampered_or_foreign_cookie_is_no_visitor()
    {
        using var host = await StartAsync();
        var (jar, _) = await GuestAsync(host);
        var genuine = jar.Visitor!;

        jar.Cookies[VisitorCookie] = genuine[..^4] + (genuine[^4] == 'A' ? "BBBB" : "AAAA");
        (await WhoAmIAsync(host, jar)).VisitorId.Should().BeNull("a value that does not unprotect is treated as absent");

        // Protected by another key ring (another application) — the same.
        var foreign = new EphemeralDataProtectionProvider().CreateProtector(SparkVisitorFeature.Purpose)
            .Protect("{\"v\":\"" + Guid.NewGuid().ToString("N") + "\",\"iat\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}");
        jar.Cookies[VisitorCookie] = foreign;
        var me = await WhoAmIAsync(host, jar);
        me.VisitorId.Should().BeNull();
        me.IdentityKey.Should().StartWith("ip:");
    }

    // ---- antiforgery on anonymous POSTs (X1) -------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_POST_carrying_a_valid_visitor_cookie_needs_the_antiforgery_token()
    {
        using var host = await StartAsync();
        var (jar, _) = await GuestAsync(host);

        // Red before X1: HasAmbientCredential was false for every anonymous caller, so this was 200 — a
        // cross-site page could change a guest's basket with the browser's own visitor cookie.
        (await SendAsync(host, HttpMethod.Post, "/spark/vt/echo", jar, sendXsrfHeader: false))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await SendAsync(host, HttpMethod.Post, "/spark/vt/echo", jar, sendXsrfHeader: true))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the browser's own client echoes XSRF-TOKEN, which the anonymous response minted");
    }

    [Fact]
    public async Task An_anonymous_POST_without_a_visitor_cookie_or_with_a_junk_one_is_not_checked()
    {
        using var host = await StartAsync();

        // A webhook or a public form: no ambient authority, nothing to forge.
        (await SendAsync(host, HttpMethod.Post, "/spark/vt/echo", Jar.Empty(), sendXsrfHeader: false))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Junk confers nothing, so it switches nothing on either.
        var junk = Jar.Empty();
        junk.Cookies[VisitorCookie] = "not-a-protected-value";
        (await SendAsync(host, HttpMethod.Post, "/spark/vt/echo", junk, sendXsrfHeader: false))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- merge into the account ---------------------------------------------------------------------------

    [Fact]
    public async Task Signing_in_merges_the_visitor_into_the_account_once_and_deletes_the_cookie()
    {
        var userId = await SeedUserAsync();
        using var host = await StartAsync();
        var (jar, visitorId) = await GuestAsync(host);
        var staleCookie = jar.Visitor!;

        (await SendAsync(host, HttpMethod.Post, "/probe/sign-in", jar)).StatusCode.Should().Be(HttpStatusCode.OK);

        var call = merges.Calls.Should().ContainSingle().Which;
        call.VisitorId.Should().Be(visitorId);
        call.UserId.Should().Be(userId, "the hook runs before the ticket is issued, with the account it is for");
        jar.Visitor.Should().BeNull("the sign-in response deletes the visitor cookie");
        (await CommittedMergesAsync()).Should().Be(1);

        // A browser that kept the cookie anyway: the next request finds the merge recorded, runs nothing, and
        // deletes the cookie again.
        jar.Cookies[VisitorCookie] = staleCookie;
        var me = await WhoAmIAsync(host, jar);
        me.UserId.Should().Be(userId);
        me.IdentityKey.Should().Be("user:" + userId);
        jar.Visitor.Should().BeNull();
        merges.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_signed_in_request_still_carrying_a_visitor_cookie_merges_it_on_sight()
    {
        // The backstop: a sign-in that bypassed the hook (or whose hook failed) leaves the cookie behind.
        var userId = await SeedUserAsync();
        using var host = await StartAsync();
        var signedIn = Jar.Empty();
        await SendAsync(host, HttpMethod.Post, "/probe/sign-in", signedIn);
        merges.Calls.Should().BeEmpty("there was no visitor to merge");

        var (guest, visitorId) = await GuestAsync(host);
        foreach (var (name, value) in signedIn.Cookies.Where(c => c.Key != XsrfCookie && !c.Key.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)))
            guest.Cookies[name] = value;

        await WhoAmIAsync(host, guest);

        merges.Calls.Should().ContainSingle().Which.VisitorId.Should().Be(visitorId);
        merges.Calls.Single().UserId.Should().Be(userId);
        guest.Visitor.Should().BeNull();
        (await CommittedMergesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Two_racing_merges_of_one_visitor_commit_exactly_once()
    {
        var userId = await SeedUserAsync();
        using var host = await StartAsync();
        var visitorId = Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<SparkVisitorMerge>().MergeAsync(visitorId, userId, CancellationToken.None);
        }));

        results.Count(r => r).Should().Be(1, "the compare-exchange record is created in the same cluster-wide commit as the handlers' writes");
        (await CommittedMergesAsync()).Should().Be(1, "the losing commit's writes never land");

        using var again = host.Services.CreateScope();
        (await again.ServiceProvider.GetRequiredService<SparkVisitorMerge>().MergeAsync(visitorId, userId, CancellationToken.None))
            .Should().BeFalse("the merge is recorded");
    }
}
