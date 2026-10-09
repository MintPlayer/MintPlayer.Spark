using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Gated dynamic client registration (RFC 7591/7592, <c>docs/identity_provider_platform_PRD.md</c> D8, I9).
/// Registering needs an initial access token, which only an approved developer obtains from the portal
/// (<c>POST /spark/identity-provider/developer/registration-token</c>), and the developer must still be
/// approved when the token is used. The registered client then manages itself with its registration
/// access token, which reaches its own registration and nothing else.
/// </summary>
public class OidcDynamicRegistrationTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A user whose developer status is <paramref name="status"/> (null: never asked).</summary>
    private async Task<string> SeedDeveloperAsync(string localPart, string? status = OidcDeveloperStatuses.Approved)
    {
        var user = await SeedUserAsync(UserEmail(localPart));
        if (status is not null)
            await SetDeveloperStatusAsync(user.Id!, status);
        return user.Id!;
    }

    private async Task SetDeveloperStatusAsync(string userId, string status)
    {
        using var session = Store.OpenAsyncSession();
        OidcDevelopers.Set(session, userId, new OidcDeveloper
        {
            Status = status,
            TermsVersion = 1,
            RequestedAt = DateTime.UtcNow,
            DecidedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync();
    }

    /// <summary>What the portal endpoint does, without the browser: tested end to end separately below.</summary>
    private async Task<string> InitialAccessTokenAsync(string developerId)
    {
        using var session = Store.OpenAsyncSession();
        var token = await OidcClientRegistration.IssueInitialAccessTokenAsync(session, developerId, CancellationToken.None);
        await session.SaveChangesAsync();
        return token;
    }

    private Dictionary<string, object> Metadata(string name, string scope = "openid") => new()
    {
        ["client_name"] = ClientId(name),
        ["redirect_uris"] = new[] { $"https://{ClientId(name)}.test/cb" },
        ["grant_types"] = new[] { "authorization_code" },
        ["token_endpoint_auth_method"] = OidcClientAuthMethods.SecretBasic,
        ["scope"] = scope,
    };

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? bearer, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> RegisterAsync(string? initialToken, object metadata)
        => SendAsync(HttpMethod.Post, "/connect/register", initialToken, metadata);

    /// <summary>Registers and returns (client_id, registration_access_token).</summary>
    private async Task<(string ClientId, string RegistrationToken)> RegisteredClientAsync(string developerId, string name)
    {
        var response = await RegisterAsync(await InitialAccessTokenAsync(developerId), Metadata(name));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await BodyAsync(response);
        return (body.GetProperty("client_id").GetString()!, body.GetProperty("registration_access_token").GetString()!);
    }

    /// <summary>A collection query filtered in memory: it can never be stale, unlike an index query.</summary>
    private async Task<OidcApplication?> StoredApplicationAsync(string clientId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.Query<OidcApplication>().ToListAsync()).SingleOrDefault(a => a.ClientId == clientId);
    }

    // ---------- registering: the initial access token gate ----------

    [Fact]
    public async Task Registering_without_an_initial_access_token_is_refused()
    {
        _ = Factory;

        var response = await RegisterAsync(null, Metadata("rp"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_token");
    }

    [Fact]
    public async Task Registering_with_a_made_up_initial_access_token_is_refused()
    {
        _ = Factory;

        var response = await RegisterAsync(OidcTokenReference.GenerateValue(), Metadata("rp"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_approved_developer_registers_a_development_client_they_administer()
    {
        var developerId = await SeedDeveloperAsync("dev");

        var response = await RegisterAsync(await InitialAccessTokenAsync(developerId), Metadata("rp"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the response carries a client secret");
        var body = await BodyAsync(response);
        var clientId = body.GetProperty("client_id").GetString()!;
        body.GetProperty("client_secret").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("registration_access_token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("registration_client_uri").GetString().Should().Be($"{Issuer}/connect/register/{clientId}");

        var stored = await StoredApplicationAsync(clientId);
        stored.Should().NotBeNull();
        stored!.Mode.Should().Be(OidcApplicationModes.Development,
            "a registered client serves its own team until it goes live through review (D4)");
        stored.Members.Count(m => m.UserId == developerId && m.Role == OidcMemberRoles.Admin).Should().Be(1);
        stored.Secrets.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_developer_whose_request_is_not_approved_cannot_register_even_holding_a_token()
    {
        var requesterId = await SeedDeveloperAsync("requester", OidcDeveloperStatuses.Requested);

        var response = await RegisterAsync(await InitialAccessTokenAsync(requesterId), Metadata("rp"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_developer_stops_the_initial_access_tokens_they_already_hold()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var token = await InitialAccessTokenAsync(developerId);

        await SetDeveloperStatusAsync(developerId, OidcDeveloperStatuses.Revoked);
        var response = await RegisterAsync(token, Metadata("rp"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "registration is the portal by protocol: the developer must be approved when the token is used");
    }

    [Fact]
    public async Task An_expired_initial_access_token_is_refused()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var value = OidcTokenReference.GenerateValue();
        await SeedAsync(session => session.StoreAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId("iat:" + value),
            Type = OidcTokenTypes.RegistrationAccessToken,
            Subject = developerId,
            Status = "valid",
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            ExpiresAt = DateTime.UtcNow.AddSeconds(-1),
            Properties = { ["kind"] = "initial" },
        }));

        (await RegisterAsync(value, Metadata("rp"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_registration_access_token_is_not_an_initial_access_token()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var (_, registrationToken) = await RegisteredClientAsync(developerId, "rp");

        (await RegisterAsync(registrationToken, Metadata("second"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a client's management token must not mint further clients");
    }

    [Fact]
    public async Task Registration_runs_the_admin_screens_rules()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var metadata = Metadata("rp");
        metadata["response_types"] = new[] { "token" };

        var response = await RegisterAsync(await InitialAccessTokenAsync(developerId), metadata);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_client_metadata");
    }

    [Fact]
    public async Task A_scope_of_an_api_the_developer_does_not_own_is_registered_pending_its_owners_approval()
    {
        var api = ClientId("partner");
        await SeedAsync(session => session.StoreAsync(new OidcResource
        {
            Id = OidcScopeCatalog.ResourceId(api),
            Kind = OidcResourceKinds.Api,
            Name = api,
            DisplayName = MintPlayer.Spark.Abstractions.TranslatedString.Create(api),
            Enabled = true,
            Scopes = [new OidcApiScope { Name = api + ".read", DisplayName = MintPlayer.Spark.Abstractions.TranslatedString.Create(api + ".read") }],
        }));
        var developerId = await SeedDeveloperAsync("dev");

        var response = await RegisterAsync(await InitialAccessTokenAsync(developerId), Metadata("rp", scope: $"openid {api}.read"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await StoredApplicationAsync((await BodyAsync(response)).GetProperty("client_id").GetString()!);
        stored!.Scopes.Single(s => s.Name == api + ".read").Status.Should().Be(OidcScopeStatuses.Pending,
            "dynamic registration must not be a way around the API owner's approval (D4)");
    }

    // ---------- managing: the registration access token (RFC 7592) ----------

    [Fact]
    public async Task A_client_reads_updates_and_deletes_its_own_registration()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var (clientId, registrationToken) = await RegisteredClientAsync(developerId, "rp");
        var url = $"/connect/register/{Uri.EscapeDataString(clientId)}";

        var read = await SendAsync(HttpMethod.Get, url, registrationToken);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(read)).GetProperty("client_id").GetString().Should().Be(clientId);

        var metadata = Metadata("rp");
        metadata["client_name"] = "Renamed " + ClientId("rp");
        var update = await SendAsync(HttpMethod.Put, url, registrationToken, metadata);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(update)).GetProperty("client_name").GetString().Should().Be("Renamed " + ClientId("rp"));

        var delete = await SendAsync(HttpMethod.Delete, url, registrationToken);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SendAsync(HttpMethod.Get, url, registrationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a deleted registration is gone");
        (await StoredApplicationAsync(clientId)).Should().BeNull();
    }

    [Fact]
    public async Task A_registration_access_token_reaches_no_other_client()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var (_, tokenA) = await RegisteredClientAsync(developerId, "rp-a");
        var (clientB, _) = await RegisteredClientAsync(developerId, "rp-b");
        var urlB = $"/connect/register/{Uri.EscapeDataString(clientB)}";

        (await SendAsync(HttpMethod.Get, urlB, tokenA)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Put, urlB, tokenA, Metadata("rp-b"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Delete, urlB, tokenA)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await StoredApplicationAsync(clientB)).Should().NotBeNull("the foreign delete must not have happened");
    }

    [Fact]
    public async Task An_initial_access_token_cannot_manage_a_registration()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var initial = await InitialAccessTokenAsync(developerId);
        var (clientId, _) = await RegisteredClientAsync(developerId, "rp");

        var response = await SendAsync(HttpMethod.Get, $"/connect/register/{Uri.EscapeDataString(clientId)}", initial);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "even the developer's own initial token is the wrong kind; RFC 7592 manages with the client's token only");
    }

    [Fact]
    public async Task Reading_a_registration_without_a_token_is_refused()
    {
        var developerId = await SeedDeveloperAsync("dev");
        var (clientId, _) = await RegisteredClientAsync(developerId, "rp");

        var response = await SendAsync(HttpMethod.Get, $"/connect/register/{Uri.EscapeDataString(clientId)}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    // ---------- the portal endpoint that issues initial access tokens ----------

    /// <summary>
    /// A cookie-carrying user agent that can also send the <c>X-XSRF-TOKEN</c> header the portal's
    /// antiforgery check reads, which <see cref="OidcTestHost.Browser"/> cannot.
    /// </summary>
    private sealed class PortalAgent(HttpClient client)
    {
        private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

        public string? Cookie(string name) => cookies.GetValueOrDefault(name);

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var raw in setCookies)
                {
                    var pair = raw.Split(';', 2)[0];
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    var value = pair[(eq + 1)..];
                    if (value.Length == 0 || raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                        cookies.Remove(pair[..eq]);
                    else
                        cookies[pair[..eq]] = value;
                }
            }
            return response;
        }

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
    }

    /// <summary>Signs in at <c>/connect/login</c>, then primes the SPA's antiforgery pair as the signed-in user.</summary>
    private async Task<(PortalAgent Agent, string Xsrf)> PortalSessionAsync(string email)
    {
        var agent = new PortalAgent(Client);
        var page = await agent.GetAsync("/connect/login?returnUrl=%2F");
        var form = new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync()),
        };
        var login = await agent.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/connect/login") { Content = new FormUrlEncodedContent(form) });
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The XSRF token is bound to the user, so it is minted after signing in.
        await agent.GetAsync("/spark");
        var xsrf = agent.Cookie("XSRF-TOKEN") ?? throw new InvalidOperationException("No XSRF-TOKEN cookie after the warm-up request.");
        return (agent, Uri.UnescapeDataString(xsrf));
    }

    private static Task<HttpResponseMessage> RequestRegistrationTokenAsync(PortalAgent agent, string? xsrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/identity-provider/developer/registration-token");
        if (xsrf is not null)
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        return agent.SendAsync(request);
    }

    [Fact]
    public async Task The_portal_issues_an_initial_access_token_to_an_approved_developer_and_it_registers_a_client()
    {
        await SeedDeveloperAsync("dev");
        var (agent, xsrf) = await PortalSessionAsync(UserEmail("dev"));

        var response = await RequestRegistrationTokenAsync(agent, xsrf);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the token is shown once");
        var body = await BodyAsync(response);
        body.GetProperty("expiresIn").GetInt32().Should().Be((int)OidcClientRegistration.InitialTokenLifetime.TotalSeconds);
        var token = body.GetProperty("initialAccessToken").GetString()!;

        (await RegisterAsync(token, Metadata("rp"))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_portal_refuses_an_initial_access_token_to_a_user_who_is_not_a_developer()
    {
        await SeedDeveloperAsync("plain", status: null);
        var (agent, xsrf) = await PortalSessionAsync(UserEmail("plain"));

        var response = await RequestRegistrationTokenAsync(agent, xsrf);

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("initialAccessToken");
    }

    [Fact]
    public async Task The_portal_refuses_an_initial_access_token_without_antiforgery()
    {
        await SeedDeveloperAsync("dev");
        var (agent, _) = await PortalSessionAsync(UserEmail("dev"));

        var response = await RequestRegistrationTokenAsync(agent, xsrf: null);

        response.IsSuccessStatusCode.Should().BeFalse("a cross-site POST must not mint a registration credential");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("initialAccessToken");
    }

    [Fact]
    public async Task The_portal_refuses_an_anonymous_caller()
    {
        var (_, xsrf) = await Factory.MintAntiforgeryAsync();
        var agent = new PortalAgent(Client);

        var response = await RequestRegistrationTokenAsync(agent, xsrf);

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("initialAccessToken");
    }
}
