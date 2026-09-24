using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// Pins that Spark's antiforgery gate actually <b>rejects</b> — end to end, over real HTTPS, with a
/// real cookie jar — rather than merely recording a verdict.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Why the negative controls are not optional.</b> ASP.NET Core's own
/// <c>AntiforgeryMiddleware</c> validates and then calls the next delegate whatever the answer: it
/// writes <c>IAntiforgeryValidationFeature</c> and leaves rejection to whoever binds the form. An
/// endpoint that binds no form has no such consumer, so a failed check is recorded and ignored. A
/// suite that only asserts "the happy path returns 200" therefore passes just as happily against a
/// pipeline that enforces nothing at all — that is not hypothetical, it is how an earlier version of
/// this work produced three green runs that meant nothing. Every case here has a paired control that
/// must fail.
/// </para>
/// <para>
/// <b>Companion test.</b> <c>MintPlayer.Spark.Tests/Extensions/XsrfSurfaceTests</c> asserts the
/// metadata on every mutating endpoint — the inventory. This file asserts that the metadata has
/// teeth on the endpoints that matter most. The inventory catches a new endpoint; this catches a
/// broken pipeline.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class XsrfEnforcementTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public XsrfEnforcementTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private const string XsrfHeader = "X-XSRF-TOKEN";

    // ---------------------------------------------------------------- login

    /// <summary>
    /// ⚠️ The regression this branch exists to prevent. <c>/spark/auth/login</c> was deliberately
    /// left ungated, on the reasoning that an anonymous caller has no token to present. It does have
    /// one — Spark mints an <c>XSRF-TOKEN</c> on every response, anonymous included — and the gap
    /// that reasoning left open is <b>login CSRF</b>: an attacker page POSTs its own credentials,
    /// the victim's browser quietly acquires a session belonging to the attacker, and every
    /// subsequent thing the victim does lands in an account the attacker can read.
    /// </summary>
    [Fact]
    public async Task Login_without_an_antiforgery_token_is_refused()
    {
        using var session = await Session.StartAsync(_fixture.Host);

        var response = await session.PostAsync(
            "/spark/auth/login?useCookies=true",
            new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass },
            token: null);

        ((int)response.StatusCode).Should().Be(400,
            "an untokened login POST is indistinguishable from one forged by a third-party page");
    }

    /// <summary>
    /// The paired positive: the very same credentials, with the token the browser would have sent,
    /// sign in. Without this the test above would also pass against a server that rejected every
    /// login for any reason at all.
    /// </summary>
    [Fact]
    public async Task Login_with_the_minted_token_succeeds()
    {
        using var session = await Session.StartAsync(_fixture.Host);

        var response = await session.PostAsync(
            "/spark/auth/login?useCookies=true",
            new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass },
            session.XsrfToken);

        ((int)response.StatusCode).Should().Be(200,
            "the anonymous token minted on the warmup response is bound to the anonymous principal "
            + "that is making this request, so it must validate");
    }

    /// <summary>⚠️ Control: a syntactically plausible but wrong token must not pass.</summary>
    [Fact]
    public async Task Login_with_a_forged_token_is_refused()
    {
        using var session = await Session.StartAsync(_fixture.Host);

        var response = await session.PostAsync(
            "/spark/auth/login?useCookies=true",
            new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass },
            token: new string('A', session.XsrfToken.Length));

        ((int)response.StatusCode).Should().Be(400,
            "an attacker who can guess the cookie's shape but not its value must still be refused");
    }

    // --------------------------------------------------------------- logout

    /// <summary>
    /// A cookie-authenticated mutating call is the textbook CSRF target, and logout is the one that
    /// can be exercised without setting up domain state first.
    /// </summary>
    [Fact]
    public async Task An_authenticated_mutating_call_without_a_token_is_refused()
    {
        using var session = await Session.StartAsync(_fixture.Host);
        await session.SignInAsync(_fixture.Host);

        var response = await session.PostAsync("/spark/auth/logout", new { }, token: null);

        ((int)response.StatusCode).Should().Be(400,
            "being signed in is exactly the condition a forged request relies on");
    }

    /// <summary>
    /// The paired positive — and the <c>csrf-refresh</c> in the middle is not boilerplate, it is the
    /// behaviour this whole branch documents.
    /// </summary>
    /// <remarks>
    /// ⚠️ The token minted on the sign-in response is bound to the <b>anonymous</b> principal, because
    /// Spark mints before the handler runs and the handler is what signs the user in. So immediately
    /// after login the client holds a token that is already wrong for the identity it now has, and
    /// the next mutating call is refused until it asks for a fresh one. That is exactly why
    /// <c>SparkAuthService.login()</c> calls <c>csrfRefresh()</c> before <c>checkAuth()</c>, and why
    /// moving the mint into <c>Response.OnStarting</c> is worth doing — it removes this round trip
    /// for sign-in (though not for sign-out; see the PRD).
    /// <para>
    /// An earlier version of this test omitted the refresh and asserted 200. It failed, correctly,
    /// and the failure was the test's, not the server's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_authenticated_mutating_call_with_a_token_succeeds()
    {
        using var session = await Session.StartAsync(_fixture.Host);
        await session.SignInAsync(_fixture.Host);

        await session.PostAsync("/spark/auth/csrf-refresh", new { }, token: null);

        var response = await session.PostAsync("/spark/auth/logout", new { }, session.XsrfToken);

        ((int)response.StatusCode).Should().Be(200,
            "after csrf-refresh the client holds a token bound to the identity it actually has");
    }

    // ---------------------------------------------------------- csrf-refresh

    /// <summary>
    /// ⚠️ The deliberate exemption, pinned so it cannot be "tidied up" into consistency. A token is
    /// bound to a principal, so a client whose principal just changed holds a stale one; the
    /// endpoint whose job is to replace it therefore cannot demand a valid one first. Requiring it
    /// would wedge every client that just signed in or out, permanently and silently.
    /// </summary>
    [Fact]
    public async Task Csrf_refresh_is_reachable_without_a_token()
    {
        using var session = await Session.StartAsync(_fixture.Host);
        await session.SignInAsync(_fixture.Host);

        var response = await session.PostAsync("/spark/auth/csrf-refresh", new { }, token: null);

        ((int)response.StatusCode).Should().Be(200,
            "this is the recovery path; gating it is a deadlock rather than a defence");
    }

    /// <summary>
    /// The reason the exemption is safe and the reason it is needed, in one assertion: a stale token
    /// is genuinely refused elsewhere, and csrf-refresh is what replaces it.
    /// </summary>
    [Fact]
    public async Task A_token_minted_before_sign_in_no_longer_works_after_it()
    {
        using var session = await Session.StartAsync(_fixture.Host);
        var anonymousToken = session.XsrfToken;

        await session.SignInAsync(_fixture.Host);

        var stale = await session.PostAsync("/spark/auth/logout", new { }, anonymousToken);
        ((int)stale.StatusCode).Should().Be(400,
            "the anonymous-bound token must not authorize a call made as an authenticated user — "
            + "that binding is what makes the double-submit cookie worth anything");

        await session.PostAsync("/spark/auth/csrf-refresh", new { }, token: null);

        var refreshed = await session.PostAsync("/spark/auth/logout", new { }, session.XsrfToken);
        ((int)refreshed.StatusCode).Should().Be(200,
            "and csrf-refresh must actually fix it, or the client has no way back");
    }

    // --------------------------------------------------------------- harness

    /// <summary>
    /// One browser-like session: its own cookie jar, so cookies accumulate exactly as a browser's
    /// would, and <see cref="XsrfToken"/> always reports the freshest <c>XSRF-TOKEN</c> the server
    /// has handed out.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly HttpClient _http;
        private readonly CookieContainer _jar;
        private readonly Uri _baseAddress;

        private Session(HttpClient http, CookieContainer jar, Uri baseAddress)
        {
            _http = http;
            _jar = jar;
            _baseAddress = baseAddress;
        }

        /// <summary>
        /// Opens a session and warms it up, which is what mints the first anonymous token. The GET
        /// matters: without a prior response there is no cookie, and every case below would fail for
        /// the wrong reason.
        /// </summary>
        public static async Task<Session> StartAsync(FleetTestHost host)
        {
            var jar = new CookieContainer();
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                CookieContainer = jar,
                UseCookies = true,
            };

            var baseAddress = new Uri(host.FleetUrl);
            var session = new Session(
                new HttpClient(handler) { BaseAddress = baseAddress }, jar, baseAddress);

            using var warmup = await session._http.GetAsync("/spark/auth/me");
            session.XsrfToken.Should().NotBeNullOrEmpty(
                "UseSpark() mints an XSRF-TOKEN on every response, including an anonymous one — if "
                + "this is empty the mint moved and every assertion in this file is meaningless");

            return session;
        }

        /// <summary>The current XSRF-TOKEN cookie value, URL-decoded as the browser would send it.</summary>
        public string XsrfToken =>
            _jar.GetCookies(_baseAddress)
                .Cast<Cookie>()
                .Where(c => c.Name == "XSRF-TOKEN")
                .Select(c => Uri.UnescapeDataString(c.Value))
                .FirstOrDefault() ?? string.Empty;

        public async Task SignInAsync(FleetTestHost host)
        {
            var response = await PostAsync(
                "/spark/auth/login?useCookies=true",
                new { email = host.AdminEmailAddress, password = host.AdminPass },
                XsrfToken);

            ((int)response.StatusCode).Should().Be(200, "the fixture's admin credentials must work");
        }

        public async Task<HttpResponseMessage> PostAsync(string path, object body, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };

            if (token is not null)
                request.Headers.Add(XsrfHeader, token);

            return await _http.SendAsync(request);
        }

        public void Dispose() => _http.Dispose();
    }
}
