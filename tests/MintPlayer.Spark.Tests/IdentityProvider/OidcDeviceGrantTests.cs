using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The device authorization grant (RFC 8628, <c>docs/identity_provider_platform_PRD.md</c> D8, I9):
/// <c>POST /connect/device_authorization</c>, the person's page at <c>/connect/device</c>, and the
/// device polling <c>/connect/token</c>. What matters for security: only an authenticated client with the
/// grant gets a code, a device code is bound to the client it was issued to, it yields tokens once,
/// and only after a signed-in person who may use the application allowed it.
/// </summary>
public class OidcDeviceGrantTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private Task<OidcApplication> SeedDeviceClientAsync(string name)
        => SeedApplicationAsync(ClientId(name), grantTypes: [OidcDeviceCodes.GrantType]);

    private Task<HttpResponseMessage> RequestDeviceCodeAsync(OidcApplication app, string scope = "openid profile", string? secret = Secret)
    {
        var form = new Dictionary<string, string> { ["client_id"] = app.ClientId, ["scope"] = scope };
        if (secret is not null)
            form["client_secret"] = secret;
        return Client.PostAsync("/connect/device_authorization", new FormUrlEncodedContent(form));
    }

    private async Task<(string DeviceCode, string UserCode)> DeviceCodeAsync(OidcApplication app)
    {
        var response = await RequestDeviceCodeAsync(app);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        return (body.GetProperty("device_code").GetString()!, body.GetProperty("user_code").GetString()!);
    }

    private Task<HttpResponseMessage> PollAsync(OidcApplication app, string deviceCode)
        => Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = OidcDeviceCodes.GrantType,
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["device_code"] = deviceCode,
        }));

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
        => (await BodyAsync(response)).TryGetProperty("error", out var e) ? e.GetString() : null;

    /// <summary>The person signs in, opens the page with the user code, and posts a decision.</summary>
    private async Task<HttpResponseMessage> DecideAsync(string email, string userCode, string decision)
    {
        var browser = await SignInAsync(email);
        var page = await browser.GetAsync($"/connect/device?user_code={Uri.EscapeDataString(userCode)}");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());

        return await browser.PostFormAsync("/connect/device", new Dictionary<string, string>
        {
            ["user_code"] = userCode,
            ["decision"] = decision,
            ["__RequestVerificationToken"] = token,
        });
    }

    // ---------- /connect/device_authorization ----------

    [Fact]
    public async Task Device_authorization_issues_a_device_code_a_user_code_and_the_verification_uri()
    {
        var app = await SeedDeviceClientAsync("tv");

        var response = await RequestDeviceCodeAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a device code is a credential");
        var body = await BodyAsync(response);
        body.GetProperty("device_code").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("user_code").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("verification_uri").GetString().Should().Be($"{Issuer}/connect/device");
        body.GetProperty("verification_uri_complete").GetString().Should().StartWith($"{Issuer}/connect/device?user_code=");
        body.GetProperty("interval").GetInt32().Should().Be(OidcDeviceCodes.IntervalSeconds);
        body.GetProperty("expires_in").GetInt32().Should().Be((int)OidcDeviceCodes.Lifetime.TotalSeconds);
    }

    [Fact]
    public async Task Device_authorization_refuses_a_wrong_secret()
    {
        var app = await SeedDeviceClientAsync("tv");

        var response = await RequestDeviceCodeAsync(app, secret: "not-the-secret");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorOf(response)).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Device_authorization_refuses_a_client_without_the_device_grant()
    {
        var app = await SeedApplicationAsync(ClientId("webapp")); // authorization_code only

        var response = await RequestDeviceCodeAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("unauthorized_client");
    }

    [Fact]
    public async Task Device_authorization_refuses_a_scope_the_client_may_not_ask_for()
    {
        var app = await SeedDeviceClientAsync("tv");

        var response = await RequestDeviceCodeAsync(app, scope: "openid email");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("invalid_scope");
    }

    // ---------- /connect/device (the person's page) ----------

    [Fact]
    public async Task Device_page_sends_an_anonymous_visitor_to_sign_in_first()
    {
        var response = await NewBrowser().GetAsync("/connect/device?user_code=BCDF-GHJK");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/connect/login?returnUrl=");
    }

    [Fact]
    public async Task Device_page_shows_an_error_for_an_unknown_user_code_and_offers_no_decision()
    {
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);

        var html = await (await browser.GetAsync("/connect/device?user_code=ZZZZ-ZZZZ")).Content.ReadAsStringAsync();

        html.Should().Contain("class=\"error\"");
        html.Should().NotContain("name=\"decision\"", "there is nothing to allow");
    }

    [Fact]
    public async Task Device_page_shows_the_application_and_its_scopes_for_a_pending_code()
    {
        var app = await SeedDeviceClientAsync("tv");
        var (_, userCode) = await DeviceCodeAsync(app);
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);

        var html = await (await browser.GetAsync($"/connect/device?user_code={Uri.EscapeDataString(userCode)}")).Content.ReadAsStringAsync();

        html.Should().Contain(app.DisplayName);
        html.Should().Contain("name=\"decision\" value=\"allow\"");
        html.Should().Contain("__RequestVerificationToken", "the decision is a state change and needs antiforgery");
    }

    // ---------- polling /connect/token ----------

    [Fact]
    public async Task Polling_before_a_decision_answers_authorization_pending_then_slow_down_when_too_fast()
    {
        var app = await SeedDeviceClientAsync("tv");
        var (deviceCode, _) = await DeviceCodeAsync(app);

        var first = await PollAsync(app, deviceCode);
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(first)).Should().Be("authorization_pending");

        var second = await PollAsync(app, deviceCode);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(second)).Should().Be("slow_down",
            $"the device polled again well inside the {OidcDeviceCodes.IntervalSeconds} s interval");
    }

    [Fact]
    public async Task An_allowed_device_code_yields_tokens_exactly_once()
    {
        var app = await SeedDeviceClientAsync("tv");
        var (deviceCode, userCode) = await DeviceCodeAsync(app);
        var email = UserEmail("alice");
        await SeedUserAsync(email);

        var decision = await DecideAsync(email, userCode, "allow");
        decision.StatusCode.Should().Be(HttpStatusCode.Redirect);
        decision.Headers.Location!.OriginalString.Should().Be("/connect/device?status=allowed");

        var redeemed = await PollAsync(app, deviceCode);
        redeemed.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(redeemed);
        body.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("id_token").GetString().Should().NotBeNullOrEmpty("openid was granted");
        body.GetProperty("token_type").GetString().Should().Be("Bearer");

        var again = await PollAsync(app, deviceCode);
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(again)).Should().Be("invalid_grant", "a device code is redeemed once");
    }

    [Fact]
    public async Task A_user_code_cannot_be_decided_again_after_it_was_allowed()
    {
        var app = await SeedDeviceClientAsync("tv");
        var (_, userCode) = await DeviceCodeAsync(app);
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        await DecideAsync(email, userCode, "allow");

        var browser = await SignInAsync(email);
        var html = await (await browser.GetAsync($"/connect/device?user_code={Uri.EscapeDataString(userCode)}")).Content.ReadAsStringAsync();

        html.Should().Contain("class=\"error\"", "only a pending device authorization can be found by its user code");
        html.Should().NotContain("name=\"decision\"");
    }

    [Fact]
    public async Task A_denied_device_code_answers_access_denied()
    {
        var app = await SeedDeviceClientAsync("tv");
        var (deviceCode, userCode) = await DeviceCodeAsync(app);
        var email = UserEmail("alice");
        await SeedUserAsync(email);

        var decision = await DecideAsync(email, userCode, "deny");
        decision.Headers.Location!.OriginalString.Should().Be("/connect/device?status=denied");

        var poll = await PollAsync(app, deviceCode);
        poll.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(poll)).Should().Be("access_denied");
    }

    [Fact]
    public async Task Allowing_a_development_application_one_is_not_a_member_of_is_recorded_as_denied()
    {
        var app = await SeedDeviceClientAsync("tv");
        using (var session = Store.OpenAsyncSession())
        {
            var stored = (await session.LoadAsync<OidcApplication>(app.Id))!;
            stored.Mode = OidcApplicationModes.Development;
            await session.SaveChangesAsync();
        }

        var (deviceCode, userCode) = await DeviceCodeAsync(app);
        var email = UserEmail("outsider");
        await SeedUserAsync(email);

        var decision = await DecideAsync(email, userCode, "allow");
        decision.Headers.Location!.OriginalString.Should().Be("/connect/device?status=denied",
            "a Development application serves its own team only (D4); the device grant is no way around that");

        (await ErrorOf(await PollAsync(app, deviceCode))).Should().Be("access_denied");
    }

    [Fact]
    public async Task A_device_code_is_bound_to_the_client_it_was_issued_to()
    {
        var owner = await SeedDeviceClientAsync("tv");
        var other = await SeedDeviceClientAsync("other-tv");
        var (deviceCode, userCode) = await DeviceCodeAsync(owner);
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        await DecideAsync(email, userCode, "allow");

        var stolen = await PollAsync(other, deviceCode);

        stolen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(stolen)).Should().Be("invalid_grant", "another client must not redeem someone else's device code");

        (await PollAsync(owner, deviceCode)).StatusCode.Should().Be(HttpStatusCode.OK,
            "and the failed attempt must not have spent the owner's code");
    }

    [Fact]
    public async Task An_expired_device_code_answers_expired_token()
    {
        var app = await SeedDeviceClientAsync("tv");
        var (deviceCode, _) = await DeviceCodeAsync(app);

        using (var session = Store.OpenAsyncSession())
        {
            // Only the document's own ExpiresAt moves; its @expires metadata stays in the future, so
            // RavenDB's expiration cannot remove it mid-test and turn this into invalid_grant.
            var device = (await session.LoadAsync<OidcToken>(OidcDeviceCodes.DeviceDocumentId(deviceCode)))!;
            device.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await session.SaveChangesAsync();
        }

        var poll = await PollAsync(app, deviceCode);

        poll.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(poll)).Should().Be("expired_token");
    }

    [Fact]
    public async Task An_unknown_device_code_answers_invalid_grant()
    {
        var app = await SeedDeviceClientAsync("tv");

        var poll = await PollAsync(app, OidcTokenReference.GenerateValue());

        (await ErrorOf(poll)).Should().Be("invalid_grant");
    }
}
