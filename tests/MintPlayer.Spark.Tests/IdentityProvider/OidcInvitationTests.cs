using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Invitations to an application's team (<c>docs/identity_provider_platform_PRD.md</c> D3, I3):
/// <see cref="OidcInvitations"/> and <c>POST /spark/identity-provider/invitations/accept</c>.
/// <para>
/// What matters here is security: inviting must not tell the inviter whether an address has an
/// account (#453), the mailed token is single use, it expires, it opens only the application it
/// was issued for, and only the account the address belongs to may redeem it. Every refusal reads
/// the same, so the accept endpoint is no oracle either.
/// </para>
/// </summary>
public class OidcInvitationTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Inviter = "SparkUsers/inviter";

    private OidcInvitations Invitations => Factory.GetService<OidcInvitations>();

    /// <summary>A user whose address the invitation lookup can already find (it rides an auto-index).</summary>
    private async Task<SparkUser> SeedFindableUserAsync(string email, bool developer)
    {
        var user = await SeedUserAsync(email);
        if (developer)
            await SetDeveloperAsync(user.Id!, OidcDeveloperStatuses.Approved);

        // The same auto-index the invitation's raw query uses (from SparkUsers where NormalizedEmail = $email),
        // waited on so the account is findable: an index query is eventually consistent.
        var normalized = email.Trim().ToUpperInvariant();
        using var session = Store.OpenAsyncSession();
        _ = await session.Query<SparkUser>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(u => u.NormalizedEmail == normalized)
            .FirstOrDefaultAsync();
        return user;
    }

    private async Task SetDeveloperAsync(string userId, string status)
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

    /// <summary>Invites <paramref name="email"/> the way the action does: in a session, saved, then the token (or null) back.</summary>
    private async Task<string?> InviteAsync(OidcApplication app, string email, string role = OidcMemberRoles.Developer)
    {
        using var session = Store.OpenAsyncSession();
        var tracked = await session.LoadAsync<OidcApplication>(app.Id);
        var token = await Invitations.InviteAsync(tracked, email, role, Inviter, CancellationToken.None);
        await session.SaveChangesAsync();
        return token;
    }

    private async Task<OidcInvitations.Acceptance> AcceptAsync(string applicationId, string token, string userId)
    {
        using var session = Store.OpenAsyncSession();
        var outcome = await Invitations.AcceptAsync(session, applicationId, token, userId, CancellationToken.None);
        if (outcome.Accepted)
            await session.SaveChangesAsync();
        return outcome;
    }

    private async Task<OidcApplication> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<OidcApplication>(id);
    }

    private static OidcApplicationMember EntryFor(OidcApplication app, string email)
        => app.Members.Single(m => m.Email == email && m.Status != OidcMemberStatuses.Revoked);

    /// <summary>
    /// #453: the stored invitation does not depend on whether the address has an account. The
    /// action's answer is the same constant notice either way (read in <c>InviteMemberAction</c>), so
    /// what remains to pin is the record the team page shows: the same shape, no user id, for both.
    /// </summary>
    [Fact]
    public async Task Inviting_records_the_same_pending_entry_for_an_existing_and_an_unknown_address()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var known = UserEmail("known");
        var unknown = UserEmail("nobody");
        await SeedFindableUserAsync(known, developer: true);

        var knownToken = await InviteAsync(app, known);
        var unknownToken = await InviteAsync(app, unknown);

        knownToken.Should().NotBeNull("an approved developer at that address is mailed a link");
        unknownToken.Should().BeNull("nobody is mailed when the address has no account");

        var stored = await LoadAsync(app.Id!);
        foreach (var entry in new[] { EntryFor(stored, known), EntryFor(stored, unknown) })
        {
            entry.Status.Should().Be(OidcMemberStatuses.Invited);
            entry.UserId.Should().BeNull("a pending invitation never discloses whether the address has an account");
            entry.InvitationHash.Should().NotBeNull("both entries look identical, mailed or not");
            entry.InvitationExpiresAt.HasValue.Should().BeTrue();
            entry.InvitedBy.Should().Be(Inviter);
        }

        EntryFor(stored, known).InvitationHash.Should().NotBe(knownToken, "only the token's SHA-256 is stored, never the bearer value");
    }

    /// <summary>D3: a Developer or Admin seat needs an approved developer; a Tester seat needs only an account.</summary>
    [Fact]
    public async Task A_developer_seat_is_mailed_only_to_an_approved_developer_a_tester_seat_to_any_account()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var plain = UserEmail("plain");
        await SeedFindableUserAsync(plain, developer: false);

        (await InviteAsync(app, plain, OidcMemberRoles.Developer)).Should().BeNull();
        (await InviteAsync(app, plain, OidcMemberRoles.Tester)).Should().NotBeNull();
    }

    /// <summary>The token is single use: accepted once, then it opens nothing.</summary>
    [Fact]
    public async Task An_invitation_is_accepted_once_and_the_token_then_opens_nothing()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var token = (await InviteAsync(app, email))!;

        var first = await AcceptAsync(app.Id!, token, user.Id!);
        first.Accepted.Should().BeTrue();
        first.ApplicationName.Should().Be(app.DisplayName);

        var stored = await LoadAsync(app.Id!);
        var member = stored.ActiveMember(user.Id);
        member.Should().NotBeNull();
        member!.Role.Should().Be(OidcMemberRoles.Developer);
        member.InvitationHash.Should().BeNull("the hash is cleared on acceptance, so the link is spent");

        var second = await AcceptAsync(app.Id!, token, user.Id!);
        second.Accepted.Should().BeFalse("a spent link must not work again");
    }

    [Fact]
    public async Task An_expired_invitation_is_refused()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var token = (await InviteAsync(app, email))!;

        using (var session = Store.OpenAsyncSession())
        {
            var tracked = await session.LoadAsync<OidcApplication>(app.Id);
            EntryFor(tracked, email).InvitationExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await session.SaveChangesAsync();
        }

        var outcome = await AcceptAsync(app.Id!, token, user.Id!);

        outcome.Accepted.Should().BeFalse();
        (await LoadAsync(app.Id!)).ActiveMember(user.Id).Should().BeNull();
    }

    /// <summary>A token opens only the application it was issued for, and trying another does not spend it.</summary>
    [Fact]
    public async Task A_token_presented_for_another_application_is_refused_and_not_spent()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var other = await SeedApplicationAsync(ClientId("other"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var token = (await InviteAsync(app, email))!;

        var wrong = await AcceptAsync(other.Id!, token, user.Id!);
        wrong.Accepted.Should().BeFalse();
        (await LoadAsync(other.Id!)).ActiveMember(user.Id).Should().BeNull();

        (await AcceptAsync(app.Id!, token, user.Id!)).Accepted.Should().BeTrue(
            "the refusal on the wrong application must not have consumed the invitation");
    }

    /// <summary>A forwarded link does not let a different account join.</summary>
    [Fact]
    public async Task Only_the_account_the_address_belongs_to_may_accept()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        await SeedFindableUserAsync(email, developer: true);
        var intruder = await SeedFindableUserAsync(UserEmail("intruder"), developer: true);
        var token = (await InviteAsync(app, email))!;

        var outcome = await AcceptAsync(app.Id!, token, intruder.Id!);

        outcome.Accepted.Should().BeFalse();
        (await LoadAsync(app.Id!)).ActiveMember(intruder.Id).Should().BeNull();
    }

    /// <summary>
    /// No oracle at the accept endpoint either: an unknown token, an expired one, the wrong
    /// application and the wrong account all read the same, and name no application.
    /// </summary>
    [Fact]
    public async Task Every_refusal_reads_the_same()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var other = await SeedApplicationAsync(ClientId("other"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var intruder = await SeedFindableUserAsync(UserEmail("intruder"), developer: true);
        var token = (await InviteAsync(app, email))!;

        var refusals = new[]
        {
            await AcceptAsync(app.Id!, OidcRequestReference.GenerateValue(), user.Id!),
            await AcceptAsync(other.Id!, token, user.Id!),
            await AcceptAsync(app.Id!, token, intruder.Id!),
            await AcceptAsync("OidcApplications/does-not-exist-" + Scope, token, user.Id!),
        };

        foreach (var refusal in refusals)
        {
            refusal.Accepted.Should().BeFalse();
            refusal.ApplicationName.Should().BeNull("naming the application would confirm the token belongs to it");
            refusal.Problem.Should().Be(refusals[0].Problem);
        }
    }

    /// <summary>Inviting the same address again replaces the pending link: one live link per address.</summary>
    [Fact]
    public async Task A_new_invitation_to_the_same_address_revokes_the_earlier_link()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var firstToken = (await InviteAsync(app, email))!;
        var secondToken = (await InviteAsync(app, email))!;

        (await AcceptAsync(app.Id!, firstToken, user.Id!)).Accepted.Should().BeFalse("the first link was replaced");
        (await AcceptAsync(app.Id!, secondToken, user.Id!)).Accepted.Should().BeTrue();
    }

    /// <summary>A Developer seat is checked again at acceptance: losing developer status in between blocks joining.</summary>
    [Fact]
    public async Task A_developer_seat_is_refused_to_an_account_that_lost_developer_status()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var token = (await InviteAsync(app, email))!;
        await SetDeveloperAsync(user.Id!, OidcDeveloperStatuses.Revoked);

        var outcome = await AcceptAsync(app.Id!, token, user.Id!);

        outcome.Accepted.Should().BeFalse();
        (await LoadAsync(app.Id!)).ActiveMember(user.Id).Should().BeNull();
    }

    // ---------- the HTTP endpoint ----------

    /// <summary>The endpoint end to end: signed in as the invited account, with an antiforgery token.</summary>
    [Fact]
    public async Task The_accept_endpoint_joins_the_signed_in_invitee()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var token = (await InviteAsync(app, email))!;

        var portal = await OidcPortalClient.SignInAsync(Client, email, Password);
        var response = await portal.PostJsonAsync("/spark/identity-provider/invitations/accept",
            new { applicationId = app.Id, token });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("accepted").GetBoolean().Should().BeTrue();

        (await LoadAsync(app.Id!)).ActiveMember(user.Id).Should().NotBeNull();
    }

    /// <summary>Anonymous callers are turned away before anything is looked up, and the invitation stays pending.</summary>
    [Fact]
    public async Task The_accept_endpoint_refuses_an_anonymous_caller()
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        var email = UserEmail("dev");
        var user = await SeedFindableUserAsync(email, developer: true);
        var token = (await InviteAsync(app, email))!;

        var anonymous = new OidcPortalClient(Client);
        var response = await anonymous.PostJsonAsync("/spark/identity-provider/invitations/accept",
            new { applicationId = app.Id, token });

        response.IsSuccessStatusCode.Should().BeFalse();
        (await LoadAsync(app.Id!)).ActiveMember(user.Id).Should().BeNull();
        EntryFor(await LoadAsync(app.Id!), email).Status.Should().Be(OidcMemberStatuses.Invited);
    }

    /// <summary>An application id outside the applications collection is refused with the generic answer.</summary>
    [Fact]
    public async Task The_accept_endpoint_refuses_an_id_outside_the_applications_collection()
    {
        var email = UserEmail("dev");
        await SeedFindableUserAsync(email, developer: true);

        var portal = await OidcPortalClient.SignInAsync(Client, email, Password);
        var response = await portal.PostJsonAsync("/spark/identity-provider/invitations/accept",
            new { applicationId = "SparkUsers/someone", token = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("accepted").GetBoolean().Should().BeFalse();
    }
}

/// <summary>
/// A cookie-carrying client for the portal's JSON API (<c>/spark/identity-provider/*</c>). Unlike
/// <see cref="OidcTestHost.Browser"/> it exposes cookie values, because those endpoints check the
/// antiforgery token in the <c>X-XSRF-TOKEN</c> header, echoed from the <c>XSRF-TOKEN</c> cookie.
/// Redirects are not followed (TestServer's client never does).
/// </summary>
internal sealed class OidcPortalClient(HttpClient client)
{
    private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

    public string? Cookie(string name) => cookies.GetValueOrDefault(name);

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<HttpResponseMessage> PostFormAsync(string url, IDictionary<string, string> form)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) });

    /// <summary>
    /// POSTs JSON with the antiforgery header. A GET goes first: the <c>XSRF-TOKEN</c> it mints is
    /// bound to the principal as it stands then, i.e. the signed-in user.
    /// </summary>
    public async Task<HttpResponseMessage> PostJsonAsync(string url, object body)
    {
        await GetAsync("/spark/identity-provider/developer");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (Cookie("XSRF-TOKEN") is { } xsrf)
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf));
        return await SendAsync(request);
    }

    /// <summary>Signs in through <c>/connect/login</c> (GET for the form and its token, then POST).</summary>
    public static async Task<OidcPortalClient> SignInAsync(HttpClient client, string email, string password)
    {
        var portal = new OidcPortalClient(client);
        var form = await portal.GetAsync("/connect/login?returnUrl=%2F");
        var token = OidcTestHost.AntiforgeryTokenFrom(await form.Content.ReadAsStringAsync());

        var response = await portal.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = token,
        });

        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Sign-in did not redirect (got {(int)response.StatusCode}).");
        return portal;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
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

                var name = pair[..eq];
                var value = pair[(eq + 1)..];
                if (value.Length == 0 || raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                    cookies.Remove(name);
                else
                    cookies[name] = value;
            }
        }

        return response;
    }
}
