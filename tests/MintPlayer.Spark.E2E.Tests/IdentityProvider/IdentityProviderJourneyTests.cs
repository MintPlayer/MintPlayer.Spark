using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.E2E.Tests.IdentityProvider;

/// <summary>
/// The identity provider's whole developer journey, end to end over HTTP against the running SparkId
/// (<c>docs/identity_provider_platform_PRD.md</c> §7 I13): developer request → approval → create an
/// application → its secret shown once → invite a tester → Development-mode sign-in → go Live →
/// granular consent → a token carrying only the granted scopes → per-scope withdrawal.
/// </summary>
/// <remarks>
/// <para>
/// One test, deliberately: every step consumes the state the previous one produced, and the point is
/// that the pieces agree with each other — the unit tests already cover each piece alone.
/// </para>
/// <para>
/// Every actor is a <see cref="SparkClient"/>, used both for the Spark API and as the "browser" on the
/// server-rendered <c>/connect/*</c> pages: it keeps its own cookie jar and never follows a redirect,
/// so each hop of the authorization flow is visible and asserted.
/// </para>
/// </remarks>
[Collection(SparkIdFleetE2ECollection.Name)]
public class IdentityProviderJourneyTests
{
    private readonly SparkIdFleetE2EFixture _fixture;
    public IdentityProviderJourneyTests(SparkIdFleetE2EFixture fixture) => _fixture = fixture;

    private SparkIdTestHost SparkId => _fixture.SparkId;

    // From the library's App_Data/Model (OidcApplication.json, OidcApplicationScope.json, OidcDeveloperRequest.json).
    private static readonly Guid ApplicationTypeId = Guid.Parse("d0d0db0e-d372-52af-8bab-7c6b7e249347");
    private static readonly Guid ApplicationScopeTypeId = Guid.Parse("eb861551-4fd7-52e0-a491-100f7e928ab8");
    private static readonly Guid DeveloperRequestTypeId = Guid.Parse("8366dd7e-a4c2-5457-b8a5-fd8d329490e1");
    private const string DeveloperRequestsQueryId = "d78fc79d-0079-53f8-a9f7-5222f251ad87";

    /// <summary>Never contacted: the flow stops at the redirect, which is where a real client would take over.</summary>
    private const string RedirectUri = "https://journey-client.example.test/signin-oidc";

    [Fact]
    public async Task A_developer_ships_an_application_and_a_user_consents_to_part_of_it_and_withdraws_part_again()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        const string password = "Journey-1!pass";
        var developerEmail = $"dev-{run}@example.test";
        var testerEmail = $"tester-{run}@example.test";
        var outsiderEmail = $"outsider-{run}@example.test";

        var developerId = await SparkId.SeedUserAsync(developerEmail, password, groupName: null);
        await SparkId.SeedUserAsync(testerEmail, password, groupName: null);
        await SparkId.SeedUserAsync(outsiderEmail, password, groupName: null);

        // An API whose scopes any team may add (AutoApprove): it gives the consent a second optional
        // scope besides profile, so withdrawing profile later NARROWS the grant instead of ending it
        // (narrowing to nothing but openid is a whole withdrawal by design, OidcGrantWithdrawal).
        await SparkId.SeedIdentityScopeAsync("openid", ["sub"], required: true);
        await SparkId.SeedIdentityScopeAsync("profile", ["name", "preferred_username", "given_name", "family_name"], required: false);
        await SparkId.SeedIdentityScopeAsync("email", ["email", "email_verified"], required: false);

        var apiName = $"journey{run}";
        var apiScope = $"{apiName}.read";
        await SparkId.SeedApiScopeAsync(apiName, apiScope, autoApprove: true);

        using var admin = NewClient();
        await admin.LoginAsync(SparkId.AdminEmailAddress, SparkId.AdminPass);
        using var developer = NewClient();
        await developer.LoginAsync(developerEmail, password);

        // ---- 1. Developer request → approval ------------------------------------------------------
        var status = await GetJsonAsync<DeveloperStatus>(developer, "/spark/identity-provider/developer");
        status.IsActive.Should().BeFalse("a fresh account is not a developer");
        status.RequireApproval.Should().BeTrue("SparkId keeps the default: a request needs an administrator's approval");

        var requested = await PostJsonAsync<DeveloperStatus>(developer, "/spark/identity-provider/developer",
            new { termsVersion = status.CurrentTermsVersion });
        requested.Status.Should().Be("Requested");
        requested.IsActive.Should().BeFalse("a pending request grants nothing yet");

        // Not a developer yet, so the applications screen is closed.
        var denied = await TryAsync(() => developer.NewPersistentObjectAsync(ApplicationTypeId));
        denied.Should().NotBeNull("an unapproved requester must not be able to create an application");

        var requestRow = await WaitForQueryRowAsync(admin, "oidc-developer-requests", developerId);
        requestRow.Should().BeTrue("the administrator's queue lists the pending request");

        var approval = await admin.ExecuteActionAsync(DeveloperRequestTypeId, "ApproveDeveloper",
            selectedItemIds: [developerId], queryId: DeveloperRequestsQueryId);
        approval.IsRetry.Should().BeFalse();

        var approved = await GetJsonAsync<DeveloperStatus>(developer, "/spark/identity-provider/developer");
        approved.Status.Should().Be("Approved");
        approved.IsActive.Should().BeTrue("the administrator approved the request");

        // ---- 2. Create the application; its secret is shown once -----------------------------------
        var draft = await developer.NewPersistentObjectAsync(ApplicationTypeId);
        Set(draft, "DisplayName", $"Journey {run}");
        Set(draft, "ClientType", "confidential");
        Set(draft, "ConsentType", "explicit");
        Set(draft, "RedirectUris", new[] { RedirectUri });
        Set(draft, "AllowedGrantTypes", new[] { "authorization_code" });
        if (draft["Scopes"] is not PersistentObjectAttributeAsDetail scopes)
            throw new InvalidOperationException("Scopes was expected to arrive as an AsDetail attribute.");
        scopes.Objects =
        [
            ScopeRow("openid", required: true),
            ScopeRow("profile", required: false),
            ScopeRow("email", required: false),
            ScopeRow(apiScope, required: false),
        ];
        scopes.IsValueChanged = true;

        var created = await Logged(developer.CreatePersistentObjectAsync(draft));
        var applicationId = created.Id!;
        var clientId = created["ClientId"].Value?.ToString();
        clientId.Should().NotBeNullOrEmpty("the client id is generated server-side (D5)");
        created["Mode"].Value?.ToString().Should().Be(OidcApplicationModes.Development, "a new application starts in Development (D4)");

        var application = (await developer.GetPersistentObjectAsync(ApplicationTypeId, applicationId))!;
        var generated = await developer.ExecuteActionAsync(ApplicationTypeId, "GenerateSecret", parent: application);
        var showSecret = generated.Operations.SingleOrDefault(o => o.Type == "showSecret");
        showSecret.Should().NotBeNull("GenerateSecret answers with the plaintext in a showSecret operation");
        var clientSecret = showSecret!.Raw.GetProperty("value").GetString()!;
        clientSecret.Should().NotBeNullOrEmpty();

        var reread = (await developer.GetPersistentObjectAsync(ApplicationTypeId, applicationId))!;
        JsonSerializer.Serialize(reread).Contains(clientSecret, StringComparison.Ordinal).Should().BeFalse(
            "the secret is shown once, in the action's response, and never again");
        var stored = (await SparkId.LoadAsync<OidcApplication>(applicationId))!;
        string.Join(' ', stored.Scopes.Select(s => $"{s.Name}:{s.Status}:{s.Required}")).Should().Be(
            $"openid:Approved:True profile:Approved:False email:Approved:False {apiScope}:Approved:False",
            "identity scopes and an AutoApprove API's scope are approved at once (D4)");
        stored.Secrets.Should().HaveCount(1);
        (stored.Secrets[0].Hash == clientSecret).Should().BeFalse("only the hash is stored");

        // ---- 3. Invite a tester; the tester accepts from the mailed link -----------------------------
        var invite = await developer.ExecuteActionAsync(ApplicationTypeId, "InviteMember", parent: reread);
        invite.IsRetry.Should().BeTrue("InviteMember asks for the address and role first");
        var form = invite.Retry!.PersistentObject!;
        Set(form, "Email", testerEmail);
        Set(form, "Role", OidcMemberRoles.Tester);
        var invited = await developer.ContinueAsync(invite, invite.Retry.DefaultOption ?? invite.Retry.AcceptedOptions[0], form);
        invited.IsRetry.Should().BeFalse();

        var mail = await SparkId.WaitForMailAsync(testerEmail, $"Journey {run}");
        var (inviteApp, inviteToken) = ReadInvitationLink(mail.HtmlBody ?? mail.TextBody ?? "");
        inviteApp.Should().Be(applicationId);

        using var tester = NewClient();
        await tester.LoginAsync(testerEmail, password);
        var accepted = await PostJsonAsync<InvitationAccepted>(tester, "/spark/identity-provider/invitations/accept",
            new { applicationId = inviteApp, token = inviteToken });
        accepted.Accepted.Should().BeTrue($"the invitee accepts their own invitation: {accepted.Problem}");

        // ---- 4. Development mode: only the team may sign in ------------------------------------------
        using var outsider = NewClient();
        var outsiderAttempt = await AuthorizeAsync(outsider, clientId!, "openid profile email", NewPkce(),
            credentials: (outsiderEmail, password));
        outsiderAttempt.Query.GetValueOrDefault("error").Should().Be("access_denied",
            $"an application in Development admits only its team (OidcApplicationAccess.MayAuthorize): {outsiderAttempt.Raw}");
        outsiderAttempt.Query.ContainsKey("code").Should().BeFalse();

        var testerPkce = NewPkce();
        var testerAttempt = await AuthorizeAsync(tester, clientId!, "openid profile", testerPkce);
        testerAttempt.Query.ContainsKey("code").Should().BeTrue(
            $"an accepted Tester may sign in to a Development application: {testerAttempt.Raw}");
        var testerToken = await ExchangeCodeAsync(clientId!, clientSecret, testerAttempt.Query["code"], testerPkce.Verifier);
        testerToken.Scopes.Should().Contain("openid");

        // ---- 5. Go Live; an outsider consents to part of what is asked -------------------------------
        var live = await developer.ExecuteActionAsync(ApplicationTypeId, "SwitchToLive",
            parent: (await developer.GetPersistentObjectAsync(ApplicationTypeId, applicationId))!);
        live.IsRetry.Should().BeFalse();
        (await SparkId.LoadAsync<OidcApplication>(applicationId))!.Mode.Should().Be(OidcApplicationModes.Live,
            "SparkId does not require a review to go live, so the Admin's own switch is the decision");

        var outsiderPkce = NewPkce();
        string? consentPage = null;
        var consented = await AuthorizeAsync(outsider, clientId!, $"openid profile email {apiScope}", outsiderPkce,
            untick: ["email"], onConsentPage: html => consentPage = html);
        consentPage.Should().NotBeNull("an explicit-consent application asks the user");
        consented.Query.ContainsKey("code").Should().BeTrue($"the outsider may sign in to a Live application: {consented.Raw}");

        var token = await ExchangeCodeAsync(clientId!, clientSecret, consented.Query["code"], outsiderPkce.Verifier);
        token.Scopes.Should().Contain("openid");
        token.Scopes.Should().Contain("profile");
        token.Scopes.Should().Contain(apiScope);
        token.Scopes.Contains("email").Should().BeFalse("the user unticked email on the consent screen");

        using (var userInfo = await GetUserInfoAsync(token.AccessToken))
        {
            userInfo.StatusCode.Should().Be(HttpStatusCode.OK);
            var claims = JsonDocument.Parse(await userInfo.Content.ReadAsStringAsync()).RootElement;
            claims.TryGetProperty("email", out _).Should().BeFalse("no email scope, no email claim");
        }

        // The list reads OidcGrants_BySubject, which is eventually consistent by design (display
        // only). Under a loaded sweep the grant written by the consent above was not indexed yet.
        await SparkId.WaitForIndexingAsync();
        var connected = await GetJsonAsync<List<ConnectedApplicationRow>>(outsider, "/spark/identity-provider/applications");
        var grant = connected.Single(a => a.ApplicationId == applicationId);
        grant.Scopes.Select(s => s.Name).Should().BeEquivalentTo(new[] { "openid", "profile", apiScope });

        // ---- 6. Per-scope withdrawal -----------------------------------------------------------------
        using (var withdraw = await outsider.SendAsync(HttpMethod.Post, "/spark/identity-provider/applications/withdraw",
            JsonContent.Create(new { applicationId, scopes = new[] { "profile" } }), requiresAntiforgery: true))
        {
            withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent, await withdraw.Content.ReadAsStringAsync());
        }

        var after = await GetJsonAsync<List<ConnectedApplicationRow>>(outsider, "/spark/identity-provider/applications");
        var narrowed = after.SingleOrDefault(a => a.ApplicationId == applicationId);
        narrowed.Should().NotBeNull("withdrawing one scope narrows the grant; it does not end it");
        narrowed!.Scopes.Select(s => s.Name).Should().BeEquivalentTo(new[] { "openid", apiScope });

        using (var revoked = await GetUserInfoAsync(token.AccessToken))
        {
            revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "a token's scopes cannot be edited, so every token issued before a withdrawal dies with it");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The authorization flow, hop by hop
    // ---------------------------------------------------------------------------------------------

    private sealed record Pkce(string Verifier, string Challenge);

    private static Pkce NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new Pkce(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private sealed record Callback(string Raw, IReadOnlyDictionary<string, string> Query);

    /// <summary>
    /// Runs <c>/connect/authorize</c> to the client's redirect URI the way a browser would: signs in on the
    /// provider's own form when sent there, and on the consent screen submits every offered scope except
    /// <paramref name="untick"/>.
    /// </summary>
    private async Task<Callback> AuthorizeAsync(
        SparkClient browser, string clientId, string scope, Pkce pkce,
        (string Email, string Password)? credentials = null,
        IReadOnlyCollection<string>? untick = null,
        Action<string>? onConsentPage = null)
    {
        var state = Guid.NewGuid().ToString("N");
        var url = "/connect/authorize?" + string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = Guid.NewGuid().ToString("N"),
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        HttpResponseMessage response = await browser.SendAsync(HttpMethod.Get, url);
        for (var hop = 0; hop < 12; hop++)
        {
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();
                var path = response.RequestMessage!.RequestUri!.AbsolutePath;

                if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location!.ToString();
                    if (location.StartsWith(RedirectUri, StringComparison.Ordinal))
                    {
                        var query = ParseQuery(new Uri(location).Query);
                        query.GetValueOrDefault("state").Should().Be(state, "the provider echoes the client's state");
                        return new Callback(location, query);
                    }
                    response = await browser.SendAsync(HttpMethod.Get, location);
                    continue;
                }

                response.StatusCode.Should().Be(HttpStatusCode.OK,
                    $"{path} answered {(int)response.StatusCode}: {body}\n--- SparkId log ---\n{SparkId.RecentLog(40)}");

                if (path == "/connect/login")
                {
                    if (credentials is not { } c)
                        throw new InvalidOperationException($"Sent to the login page without credentials to sign in with.\n{body}");
                    var fields = HiddenFields(body);
                    fields.Add(new("identifier", c.Email));
                    fields.Add(new("password", c.Password));
                    response = await browser.SendAsync(HttpMethod.Post, "/connect/login", new FormUrlEncodedContent(fields));
                    continue;
                }

                if (path == "/connect/consent")
                {
                    onConsentPage?.Invoke(body);
                    var fields = HiddenFields(body);
                    foreach (Match box in Regex.Matches(body, "<input type=\"checkbox\" name=\"scopes\" value=\"([^\"]*)\" checked( disabled)? />"))
                    {
                        var value = WebUtility.HtmlDecode(box.Groups[1].Value);
                        var disabled = box.Groups[2].Success;
                        // A disabled box (required, or already granted) travels as its hidden field.
                        if (!disabled && untick?.Contains(value) != true)
                            fields.Add(new("scopes", value));
                    }
                    if (untick is not null)
                    {
                        foreach (var scopeName in untick)
                            Regex.IsMatch(body, $"name=\"scopes\" value=\"{Regex.Escape(scopeName)}\" checked />").Should().BeTrue(
                                $"'{scopeName}' is offered as an optional scope the user can untick");
                    }
                    fields.Add(new("decision", "allow"));
                    response = await browser.SendAsync(HttpMethod.Post, "/connect/consent", new FormUrlEncodedContent(fields));
                    continue;
                }

                throw new InvalidOperationException($"Unexpected page {path} in the authorization flow:\n{body}");
            }
        }

        throw new InvalidOperationException("The authorization flow did not reach the redirect URI within 12 hops.");
    }

    private sealed record IssuedToken(string AccessToken, IReadOnlyList<string> Scopes);

    /// <summary>Redeems a code at <c>/connect/token</c> with <c>client_secret_basic</c> and the PKCE verifier.</summary>
    private async Task<IssuedToken> ExchangeCodeAsync(string clientId, string clientSecret, string code, string verifier)
    {
        using var http = new HttpClient { BaseAddress = new Uri(SparkId.Issuer) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                new KeyValuePair<string, string>("code_verifier", verifier),
            ]),
        };
        // RFC 6749 §2.3.1: both halves form-urlencoded before they are joined and base64-encoded.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}")));

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(
            $"the token request failed ({(int)response.StatusCode}): {body}\n--- SparkId log ---\n{SparkId.RecentLog(40)}");

        var accessToken = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;

        // The scopes the token itself carries — what a resource server reads — not the response's echo.
        var parts = accessToken.Split('.');
        parts.Length.Should().Be(3, "SparkId issues JWT access tokens");
        var payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
        var scopeClaim = payload.GetProperty("scope");
        IReadOnlyList<string> scopes = scopeClaim.ValueKind == JsonValueKind.Array
            ? scopeClaim.EnumerateArray().Select(e => e.GetString()!).ToList()
            : scopeClaim.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new IssuedToken(accessToken, scopes);
    }

    private async Task<HttpResponseMessage> GetUserInfoAsync(string accessToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(SparkId.Issuer) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A "browser": https (the antiforgery cookie is minted on a secure request only), never following
    /// a redirect, so every hop of the authorization flow is observed. The token endpoint is called on
    /// the plain-http issuer, as a back-channel client would.
    /// </summary>
    private SparkClient NewClient() => new(new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = new Uri(SparkId.AppUrl) }, ownsClient: true);

    private sealed record DeveloperStatus(string? Status, int CurrentTermsVersion, bool RequireApproval, bool IsActive);
    private sealed record InvitationAccepted(bool Accepted, string? Application, string? Problem);
    private sealed record ConnectedScopeRow(string Name, bool Required);
    private sealed record ConnectedApplicationRow(string ApplicationId, List<ConnectedScopeRow> Scopes);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<T> GetJsonAsync<T>(SparkClient client, string url)
    {
        using var response = await client.SendAsync(HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"GET {url} answered {(int)response.StatusCode}: {body}\n{SparkId.RecentLog(30)}");
        return JsonSerializer.Deserialize<T>(body, Web)!;
    }

    private async Task<T> PostJsonAsync<T>(SparkClient client, string url, object payload)
    {
        using var response = await client.SendAsync(HttpMethod.Post, url, JsonContent.Create(payload), requiresAntiforgery: true);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"POST {url} answered {(int)response.StatusCode}: {body}\n{SparkId.RecentLog(30)}");
        return JsonSerializer.Deserialize<T>(body, Web)!;
    }

    /// <summary>A Spark call whose failure carries SparkId's own log: a 4xx/5xx body says little on its own.</summary>
    private async Task<T> Logged<T>(Task<T> call)
    {
        try { return await call; }
        catch (SparkClientException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n--- SparkId log ---\n{SparkId.RecentLog(60)}", ex);
        }
    }

    private static async Task<Exception?> TryAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a row of the query. The developer-requests query reads the user
    /// collection through an index, which is eventually consistent; waiting on indexing between attempts
    /// is the failure bound, never a fixed sleep.
    /// </summary>
    private async Task<bool> WaitForQueryRowAsync(SparkClient client, string queryAlias, string id)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            QueryResult result;
            try { result = await client.ExecuteQueryAsync(queryAlias); }
            catch (SparkClientException ex)
            {
                throw new InvalidOperationException($"Query {queryAlias} failed: {ex.Message}\n--- SparkId log ---\n{SparkId.RecentLog(60)}", ex);
            }
            if (result.Items.Any(i => i.Id == id))
                return true;
            await SparkId.WaitForIndexingAsync();
        }
        return false;
    }

    private static void Set(PersistentObject po, string attribute, object? value)
    {
        po[attribute].Value = value;
        po[attribute].IsValueChanged = true;
    }

    private static PersistentObject ScopeRow(string name, bool required) => new()
    {
        Name = "OidcApplicationScope",
        ObjectTypeId = ApplicationScopeTypeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Name", Value = name, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Required", Value = required, DataType = "boolean", IsValueChanged = true },
        ],
    };

    private static List<KeyValuePair<string, string>> HiddenFields(string html)
        => Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\" />")
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .ToList();

    /// <summary>The application id and token from the invitation mail's link (<c>/developers/invitations/{token}?app=…</c>).</summary>
    private static (string ApplicationId, string Token) ReadInvitationLink(string body)
    {
        var match = Regex.Match(WebUtility.HtmlDecode(body), @"/developers/invitations/([^?""\s<]+)\?app=([^""&\s<]+)");
        if (!match.Success)
            throw new InvalidOperationException($"No invitation link in the mail:\n{body}");
        return (Uri.UnescapeDataString(match.Groups[2].Value), Uri.UnescapeDataString(match.Groups[1].Value));
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
