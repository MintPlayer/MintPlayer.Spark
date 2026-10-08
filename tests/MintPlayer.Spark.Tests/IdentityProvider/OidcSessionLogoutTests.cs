using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// I10: provider sessions and logout (OIDC Back-Channel Logout 1.0, D8). A sign-in at the provider
/// gets a <c>sid</c>; tokens issued under it carry it; <c>/connect/logout</c> ends the session, revokes
/// its refresh tokens and posts a signed logout token to every client with a back-channel URI.
/// <para>
/// The back-channel POST is captured by a stub primary handler on the named client the provider uses
/// (<c>Spark.IdentityProvider.BackChannelLogout</c>), so nothing leaves the process. Each case gives its
/// application a back-channel URI of its own and reads only the posts to it.
/// </para>
/// </summary>
public class OidcSessionLogoutTests(OidcSessionLogoutTests.LogoutHost host)
    : OidcTestHost(host), IClassFixture<OidcSessionLogoutTests.LogoutHost>
{
    private readonly LogoutHost logoutHost = host;

    public sealed class LogoutHost : OidcSharedHost
    {
        /// <summary>Every back-channel POST the provider made: the target URI and the form it posted.</summary>
        public ConcurrentQueue<(Uri Uri, Dictionary<string, string> Form)> BackChannelPosts { get; } = new();

        protected override void ConfigureServices(IServiceCollection services)
            => services.AddHttpClient("Spark.IdentityProvider.BackChannelLogout")
                .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(BackChannelPosts));
    }

    private sealed class RecordingHandler(ConcurrentQueue<(Uri Uri, Dictionary<string, string> Form)> posts) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            posts.Enqueue((request.RequestUri!, form.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => form[k]!)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private const string Secret = "s3cret-value-for-tests";

    private async Task<OidcApplication> SeedRelyingPartyAsync(string name, string? backChannelUri = null)
    {
        var app = await SeedApplicationAsync(ClientId(name),
            allowedScopes: ["openid", "profile", "offline_access"],
            grantTypes: ["authorization_code", "refresh_token"]);

        if (backChannelUri is not null)
        {
            using var session = Store.OpenAsyncSession();
            var stored = await session.LoadAsync<OidcApplication>(app.Id);
            stored.BackChannelLogoutUri = backChannelUri;
            await session.SaveChangesAsync();
            app.BackChannelLogoutUri = backChannelUri;
        }

        return app;
    }

    /// <summary>authorize → consent in <paramref name="browser"/>'s session, so the code carries that session's sid.</summary>
    private static async Task<string> AuthorizeAsync(Browser browser, OidcApplication app, string[] scopes)
    {
        var url = $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}&response_type=code"
                + $"&scope={Uri.EscapeDataString(string.Join(' ', scopes))}";

        var authorize = await browser.GetAsync(url);
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = authorize.Headers.Location!.OriginalString;

        if (location.StartsWith("/connect/consent", StringComparison.Ordinal))
        {
            var requestId = System.Web.HttpUtility.ParseQueryString(location[(location.IndexOf('?') + 1)..])["request_id"]!;
            var page = await browser.GetAsync(location);
            var token = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());

            var pairs = new List<KeyValuePair<string, string>>
            {
                new("request_id", requestId),
                new("decision", "allow"),
                new("__RequestVerificationToken", token),
            };
            pairs.AddRange(scopes.Select(s => new KeyValuePair<string, string>("scopes", s)));

            var consent = await browser.PostRawAsync("/connect/consent", pairs);
            location = consent.Headers.Location!.OriginalString;
        }

        return System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]
            ?? throw new InvalidOperationException($"No code in redirect: {location}");
    }

    private async Task<JsonElement> RedeemAsync(OidcApplication app, string code)
    {
        var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = app.RedirectUris[0],
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private Task<HttpResponseMessage> RefreshAsync(OidcApplication app, string refreshToken)
        => Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refreshToken,
        }));

    private static string SidOf(string idToken)
        => new JsonWebToken(idToken).TryGetPayloadValue<string>("sid", out var sid) ? sid : throw new InvalidOperationException("No sid in the id_token.");

    /// <summary>
    /// Builds the index the logout sweep reads and lets it catch up, so a case about what logout does is
    /// not decided by whether a just-minted refresh token was indexed yet. The sweep's staleness is its
    /// own case below.
    /// </summary>
    private async Task WarmSessionSweepIndexAsync(string sid)
    {
        using (var session = Store.OpenAsyncSession())
        {
            _ = await session.Query<OidcToken>()
                .Where(t => t.SessionId == sid && t.Type == OidcTokenTypes.RefreshToken && t.Status == "valid")
                .ToListAsync();
        }
        await Store.WaitForIndexingAsync();
    }

    [Fact]
    public async Task The_id_token_carries_the_provider_sid_and_the_session_records_the_client()
    {
        var app = await SeedRelyingPartyAsync("rp");
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);

        var tokens = await RedeemAsync(app, await AuthorizeAsync(browser, app, ["openid", "profile"]));

        var sid = SidOf(tokens.GetProperty("id_token").GetString()!);
        sid.Should().NotBeNullOrEmpty();

        using var session = Store.OpenAsyncSession();
        var record = await session.LoadAsync<OidcToken>(OidcSessionStore.DocumentId(sid));
        record.Should().NotBeNull("a client receiving tokens joins the provider session");
        record!.Type.Should().Be(OidcTokenTypes.Session);
        record.Status.Should().Be("valid");
        record.Properties["clients"].Split(' ').Should().Contain(app.Id!);
    }

    [Fact]
    public async Task Logout_posts_a_signed_logout_token_to_the_back_channel_uri()
    {
        var backChannel = $"https://{ClientId("rp")}.test/backchannel-logout";
        var app = await SeedRelyingPartyAsync("rp", backChannel);
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);
        var tokens = await RedeemAsync(app, await AuthorizeAsync(browser, app, ["openid"]));
        var sid = SidOf(tokens.GetProperty("id_token").GetString()!);

        var logout = await browser.GetAsync("/connect/logout");
        logout.StatusCode.Should().Be(HttpStatusCode.OK);

        // Every client of the session with a back-channel URI gets exactly one logout token.
        var post = logoutHost.BackChannelPosts.Where(p => p.Uri == new Uri(backChannel)).ToList().Should().ContainSingle().Which;
        post.Form.Should().ContainKey("logout_token");
        var logoutToken = post.Form["logout_token"];

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(logoutToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = app.ClientId,
            IssuerSigningKeys = Factory.GetService<OidcKeyRing>().ValidationKeys,
        });
        validation.IsValid.Should().BeTrue("the logout token is signed by the provider's ring");

        var jwt = (JsonWebToken)validation.SecurityToken;
        jwt.Typ.Should().Be("logout+jwt");
        jwt.TryGetPayloadValue<string>("sid", out var tokenSid).Should().BeTrue();
        tokenSid.Should().Be(sid);
        jwt.Id.Should().NotBeNullOrEmpty("Back-Channel Logout 1.0 §2.4 requires a jti");
        var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(logoutToken.Split('.')[1])).RootElement;
        payload.GetProperty("events").TryGetProperty("http://schemas.openid.net/event/backchannel-logout", out _).Should().BeTrue();
        payload.TryGetProperty("nonce", out _).Should().BeFalse("a logout token must not carry a nonce");
    }

    [Fact]
    public async Task Logout_revokes_the_sessions_refresh_tokens()
    {
        var app = await SeedRelyingPartyAsync("rp");
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);
        var tokens = await RedeemAsync(app, await AuthorizeAsync(browser, app, ["openid", "offline_access"]));
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        var sid = SidOf(tokens.GetProperty("id_token").GetString()!);
        await WarmSessionSweepIndexAsync(sid);

        (await browser.GetAsync("/connect/logout")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(refreshToken)))!.Status.Should().Be("revoked");
            (await session.LoadAsync<OidcToken>(OidcSessionStore.DocumentId(sid)))!.Status.Should().Be("revoked");
        }

        (await RefreshAsync(app, refreshToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a signed-out session's refresh token mints nothing");
    }

    /// <summary>
    /// The sweep reads an index and may miss a refresh token minted moments before the logout; its own
    /// comment says the refresh grant "re-checks the session-bound state", so a missed token must still be
    /// refused. Simulated by putting the token back to valid after the logout, as a stale index would
    /// have left it.
    /// </summary>
    [Fact]
    public async Task A_refresh_token_the_logout_sweep_missed_is_still_refused_after_logout()
    {
        var app = await SeedRelyingPartyAsync("rp");
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);
        var tokens = await RedeemAsync(app, await AuthorizeAsync(browser, app, ["openid", "offline_access"]));
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        (await browser.GetAsync("/connect/logout")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var session = Store.OpenAsyncSession())
        {
            var missed = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(refreshToken));
            missed!.Status = "valid";
            await session.SaveChangesAsync();
        }

        (await RefreshAsync(app, refreshToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the session ended, so a refresh token bound to it must be refused even if the sweep missed it");
    }

    [Fact]
    public async Task Logout_leaves_another_session_of_the_same_user_alone()
    {
        var app = await SeedRelyingPartyAsync("rp");
        var email = UserEmail("alice");
        await SeedUserAsync(email);

        var laptop = await SignInAsync(email);
        var laptopTokens = await RedeemAsync(app, await AuthorizeAsync(laptop, app, ["openid", "offline_access"]));
        var phone = await SignInAsync(email);
        var phoneTokens = await RedeemAsync(app, await AuthorizeAsync(phone, app, ["openid", "offline_access"]));

        var laptopSid = SidOf(laptopTokens.GetProperty("id_token").GetString()!);
        SidOf(phoneTokens.GetProperty("id_token").GetString()!).Should().NotBe(laptopSid, "each sign-in is its own session");
        await WarmSessionSweepIndexAsync(laptopSid);

        (await laptop.GetAsync("/connect/logout")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RefreshAsync(app, phoneTokens.GetProperty("refresh_token").GetString()!)).StatusCode.Should().Be(HttpStatusCode.OK,
            "signing out on one device ends that session only");
    }
}
