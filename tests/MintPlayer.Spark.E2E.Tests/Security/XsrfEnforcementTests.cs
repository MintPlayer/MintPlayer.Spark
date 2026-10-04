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
    /// The paired positive, through the path the Angular client takes: <c>SparkAuthService.login()</c>
    /// still calls <c>csrf-refresh</c> after signing in, so this keeps that sequence working.
    /// </summary>
    /// <remarks>
    /// <s>⚠️ The token minted on the sign-in response is bound to the <b>anonymous</b> principal, because
    /// Spark mints before the handler runs</s> — superseded by #452: the mint now runs in
    /// <c>Response.OnStarting</c>, after the handler, so the refresh is no longer needed after sign-in
    /// (<see cref="A_mutating_call_right_after_sign_in_succeeds_without_csrf_refresh"/>). It stays
    /// here because the client still makes it and must keep getting a 200.
    /// <para>
    /// Before #452 a version of this test omitted the refresh and asserted 200. It failed, correctly,
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

    /// <summary>
    /// #452 (plan M2): the token on the sign-in response is minted <b>after</b> the handler signed the
    /// user in, so it is bound to the signed-in principal and the very next mutating call passes —
    /// no <c>csrf-refresh</c> in between.
    /// </summary>
    /// <remarks>
    /// Before #452 Spark minted before the handler, and this exact sequence returned 400:
    /// <see cref="An_authenticated_mutating_call_with_a_token_succeeds"/> had to refresh first.
    /// </remarks>
    [Fact]
    public async Task A_mutating_call_right_after_sign_in_succeeds_without_csrf_refresh()
    {
        using var session = await Session.StartAsync(_fixture.Host);
        await session.SignInAsync(_fixture.Host);

        var response = await session.PostAsync("/spark/auth/logout", new { }, session.XsrfToken);

        ((int)response.StatusCode).Should().Be(200,
            "the sign-in response's token is bound to the identity the sign-in established");
    }

    /// <summary>
    /// #452 (PRD §5, reason 2): the antiforgery gate's own 400 carries a fresh token, so a client
    /// holding a stale one recovers by retrying instead of having to know about <c>csrf-refresh</c>.
    /// </summary>
    /// <remarks>
    /// Sign-out is the realistic way to end up stale: <c>SignOutAsync</c> does not reset
    /// <c>HttpContext.User</c>, so the token minted on the sign-out response is still bound to the
    /// user who just left, and the next anonymous POST (signing in again) is refused. That refusal
    /// only carries a new cookie when <c>UseAntiforgeryGenerator()</c> is registered <b>above</b>
    /// <c>UseSparkAntiforgery()</c>: the gate short-circuits, so a generator registered below it never
    /// gets to add its <c>OnStarting</c> callback. With it below, the retry repeats the stale token
    /// and is refused again.
    /// </remarks>
    [Fact]
    public async Task The_gates_refusal_carries_a_fresh_token_so_signing_in_again_after_sign_out_recovers()
    {
        using var session = await Session.StartAsync(_fixture.Host);
        await session.SignInAsync(_fixture.Host);

        var signOut = await session.PostAsync("/spark/auth/logout", new { }, session.XsrfToken);
        ((int)signOut.StatusCode).Should().Be(200, "the signed-in token is valid for sign-out");

        var credentials = new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass };
        var staleToken = session.XsrfToken;

        var refused = await session.PostAsync("/spark/auth/login?useCookies=true", credentials, staleToken);
        ((int)refused.StatusCode).Should().Be(400,
            "the token minted on the sign-out response is still bound to the user who signed out");
        session.XsrfToken.Should().NotBe(staleToken,
            "the gate's 400 must carry a fresh XSRF-TOKEN, or the client cannot recover by retrying");

        var retried = await session.PostAsync("/spark/auth/login?useCookies=true", credentials, session.XsrfToken);
        ((int)retried.StatusCode).Should().Be(200,
            "the token from the refusal is bound to the anonymous principal now making the request");
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
