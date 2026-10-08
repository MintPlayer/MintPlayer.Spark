using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.CustomActions;
using MintPlayer.Spark.IdentityProvider.Interceptors;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The decisions the portal queues for someone else (<c>docs/identity_provider_platform_PRD.md</c> D2, D4):
/// developer requests, API scopes asked for by an application whose team does not own the API, and
/// going Live under <c>Apps:RequireReviewToGoLive</c>.
/// <para>
/// The custom actions are thin shells over <c>OidcDeveloperDecision</c>, <c>OidcScopeDecision</c> and
/// <c>OidcGoLiveDecision</c>, so those are called directly with a stub <see cref="IManager"/>: what is
/// asserted is the state each decision leaves in the database, and who may make it.
/// </para>
/// </summary>
public class OidcApprovalTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Administrator = "SparkUsers/idp-admin";

    private static IHttpContextAccessor As(string userId) => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
        },
    };

    /// <summary>The caller's portal access; <paramref name="administrator"/> answers <c>ManageAll/IdentityProvider</c>.</summary>
    private static OidcPortalAccess Access(string userId, bool administrator)
    {
        var accessControl = Substitute.For<IAccessControl>();
        accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(administrator);
        return new OidcPortalAccess(httpContextAccessor: As(userId), accessControl: accessControl);
    }

    private static CustomActionArgs Selected(params string[] ids) => new()
    {
        SelectedItems = [.. ids.Select(id => new QueryResultItem { Id = id, Values = [] })],
    };

    private OidcAudit Audit => Factory.GetService<OidcAudit>();
    private OidcDevelopers Developers => Factory.GetService<OidcDevelopers>();

    private async Task<OidcApplication> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<OidcApplication>(id);
    }

    private async Task UpdateAsync(string id, Action<OidcApplication> change)
    {
        using var session = Store.OpenAsyncSession();
        change(await session.LoadAsync<OidcApplication>(id));
        await session.SaveChangesAsync();
    }

    private static OidcApplicationMember Member(string userId, string role) => new()
    {
        MemberId = Guid.NewGuid().ToString("N"),
        UserId = userId,
        Role = role,
        Status = OidcMemberStatuses.Active,
        AcceptedAt = DateTime.UtcNow,
    };

    // ---------- developer requests (D2) ----------

    private async Task<string> SeedDeveloperRequestAsync(string localPart, string status = OidcDeveloperStatuses.Requested)
    {
        var user = await SeedUserAsync(UserEmail(localPart));
        using var session = Store.OpenAsyncSession();
        OidcDevelopers.Set(session, user.Id!, new OidcDeveloper { Status = status, TermsVersion = 1, RequestedAt = DateTime.UtcNow });
        await session.SaveChangesAsync();
        return user.Id!;
    }

    private async Task DecideDeveloperAsync(bool approve, params string[] userIds)
    {
        using var session = Store.OpenAsyncSession();
        await OidcDeveloperDecision.DecideAsync(
            Selected(userIds), approve, Substitute.For<IManager>(), session, As(Administrator), Developers, Audit,
            Factory.GetService<OidcPortalMail>(), Factory.GetService<OidcPortalLinks>(), CancellationToken.None);
    }

    [Fact]
    public async Task Approving_a_developer_request_makes_the_user_an_active_developer()
    {
        var userId = await SeedDeveloperRequestAsync("dev");

        await DecideDeveloperAsync(approve: true, userId);

        var developer = await Developers.GetAsync(userId);
        developer!.Status.Should().Be(OidcDeveloperStatuses.Approved);
        developer.DecidedBy.Should().Be(Administrator);
        developer.DecidedAt.HasValue.Should().BeTrue();
        Developers.IsActive(developer).Should().BeTrue();
    }

    [Fact]
    public async Task Rejecting_a_developer_request_leaves_the_user_without_portal_access()
    {
        var userId = await SeedDeveloperRequestAsync("dev");

        await DecideDeveloperAsync(approve: false, userId);

        var developer = await Developers.GetAsync(userId);
        developer!.Status.Should().Be(OidcDeveloperStatuses.Rejected);
        Developers.IsActive(developer).Should().BeFalse();
    }

    /// <summary>
    /// Only a pending request is decided: a stale selection cannot approve someone already rejected
    /// or revoked (the row is re-read, strongly consistent, on the server).
    /// </summary>
    [Theory]
    [InlineData(OidcDeveloperStatuses.Rejected)]
    [InlineData(OidcDeveloperStatuses.Revoked)]
    public async Task Approving_never_overturns_a_request_that_is_no_longer_pending(string status)
    {
        var userId = await SeedDeveloperRequestAsync("dev", status);

        await DecideDeveloperAsync(approve: true, userId);

        (await Developers.GetAsync(userId))!.Status.Should().Be(status);
    }

    /// <summary>The request half over HTTP: with <c>RequireApproval</c> (the default) a request waits, and the approval activates it.</summary>
    [Fact]
    public async Task A_developer_request_waits_for_approval_and_is_active_after_it()
    {
        var email = UserEmail("dev");
        var user = await SeedUserAsync(email);
        var portal = await OidcPortalClient.SignInAsync(Client, email, Password);

        var requested = await portal.PostJsonAsync("/spark/identity-provider/developer", new { termsVersion = 1 });
        requested.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await requested.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("status").GetString().Should().Be(OidcDeveloperStatuses.Requested);
        body.GetProperty("isActive").GetBoolean().Should().BeFalse("a request needs an administrator's approval");

        await DecideDeveloperAsync(approve: true, user.Id!);

        var status = JsonDocument.Parse(await (await portal.GetAsync("/spark/identity-provider/developer")).Content.ReadAsStringAsync()).RootElement;
        status.GetProperty("status").GetString().Should().Be(OidcDeveloperStatuses.Approved);
        status.GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    /// <summary>Accepting terms nobody showed the user is not consent: an outdated version is a 409.</summary>
    [Fact]
    public async Task A_developer_request_for_outdated_terms_is_refused()
    {
        var email = UserEmail("dev");
        var user = await SeedUserAsync(email);
        var portal = await OidcPortalClient.SignInAsync(Client, email, Password);

        var response = await portal.PostJsonAsync("/spark/identity-provider/developer", new { termsVersion = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Developers.GetAsync(user.Id!)).Should().BeNull();
    }

    /// <summary>An administrator's revocation is not undone by asking again.</summary>
    [Fact]
    public async Task A_revoked_developer_cannot_request_again()
    {
        var email = UserEmail("dev");
        var user = await SeedUserAsync(email);
        using (var session = Store.OpenAsyncSession())
        {
            OidcDevelopers.Set(session, user.Id!, new OidcDeveloper { Status = OidcDeveloperStatuses.Revoked, TermsVersion = 1 });
            await session.SaveChangesAsync();
        }
        var portal = await OidcPortalClient.SignInAsync(Client, email, Password);

        var response = await portal.PostJsonAsync("/spark/identity-provider/developer", new { termsVersion = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Developers.GetAsync(user.Id!))!.Status.Should().Be(OidcDeveloperStatuses.Revoked);
    }

    // ---------- cross-owner API scopes (D4) ----------

    /// <summary>A resource name unique to the case; an API scope is "{resource}.{name}".</summary>
    private string ApiName => "billing-" + Scope;

    private async Task SeedApiAsync(string owner, bool autoApprove = false)
    {
        _ = Factory;
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new OidcResource
        {
            Id = OidcScopeCatalog.ResourceId(ApiName),
            Kind = OidcResourceKinds.Api,
            Name = ApiName,
            DisplayName = TranslatedString.Create(ApiName),
            Enabled = true,
            Owners = [owner],
            AutoApprove = autoApprove,
            Scopes = [new OidcApiScope { Name = ApiName + ".read", DisplayName = TranslatedString.Create("read") }],
        });
        await session.SaveChangesAsync();
    }

    private async Task<List<OidcApplicationScope>> MarkAsync(OidcApplication app, string requestedBy, params string[] scopes)
    {
        var added = scopes.Select(s => new OidcApplicationScope { Name = s }).ToList();
        app.Scopes.AddRange(added);
        using var session = Store.OpenAsyncSession();
        await OidcApplicationInterceptors.MarkCrossOwnerScopesAsync(session, app, added, requestedBy);
        return added;
    }

    [Fact]
    public async Task An_api_scope_the_team_does_not_own_starts_pending()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var app = new OidcApplication { ClientId = ClientId("client"), Members = [Member("SparkUsers/dev", OidcMemberRoles.Admin)] };

        var added = await MarkAsync(app, "SparkUsers/dev", "openid", ApiName + ".read");

        added[0].Status.Should().Be(OidcScopeStatuses.Approved, "identity scopes need nobody's approval");
        added[1].Status.Should().Be(OidcScopeStatuses.Pending);
        added[1].Review.Should().NotBeNull();
        added[1].Review!.Status.Should().Be(OidcScopeStatuses.Pending);
        added[1].Review!.RequestedBy.Should().Be("SparkUsers/dev");
    }

    [Fact]
    public async Task An_api_scope_of_the_teams_own_api_is_approved_at_once()
    {
        await SeedApiAsync(owner: "SparkUsers/dev");
        var app = new OidcApplication { ClientId = ClientId("client"), Members = [Member("SparkUsers/dev", OidcMemberRoles.Developer)] };

        var added = await MarkAsync(app, "SparkUsers/dev", ApiName + ".read");

        added[0].Status.Should().Be(OidcScopeStatuses.Approved);
    }

    [Fact]
    public async Task An_api_scope_of_an_auto_approve_api_is_approved_at_once()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner", autoApprove: true);
        var app = new OidcApplication { ClientId = ClientId("client"), Members = [Member("SparkUsers/dev", OidcMemberRoles.Admin)] };

        var added = await MarkAsync(app, "SparkUsers/dev", ApiName + ".read");

        added[0].Status.Should().Be(OidcScopeStatuses.Approved);
    }

    /// <summary>
    /// Pending means "the team may develop against it, nobody else gets it": only the application's
    /// own members can authorize a pending scope, and a rejected scope is available to nobody.
    /// </summary>
    [Fact]
    public void A_pending_scope_is_available_to_the_team_only_and_a_rejected_one_to_nobody()
    {
        var app = new OidcApplication
        {
            ClientId = "client",
            Mode = OidcApplicationModes.Live,
            Members = [Member("SparkUsers/dev", OidcMemberRoles.Developer)],
            Scopes =
            [
                new OidcApplicationScope { Name = "openid" },
                new OidcApplicationScope { Name = "billing.read", Status = OidcScopeStatuses.Pending },
                new OidcApplicationScope { Name = "billing.write", Status = OidcScopeStatuses.Rejected },
            ],
        };
        string[] requested = ["openid", "billing.read", "billing.write"];

        OidcApplicationAccess.AvailableScopes(app, "SparkUsers/dev", requested)
            .Should().Equal("openid", "billing.read");
        OidcApplicationAccess.AvailableScopes(app, "SparkUsers/outsider", requested)
            .Should().Equal("openid");
    }

    private async Task<OidcApplication> SeedPendingScopeApplicationAsync()
    {
        var app = await SeedApplicationAsync(ClientId("client"));
        await UpdateAsync(app.Id!, a =>
        {
            a.Members.Add(Member("SparkUsers/dev", OidcMemberRoles.Admin));
            a.Scopes.Add(new OidcApplicationScope
            {
                Name = ApiName + ".read",
                Status = OidcScopeStatuses.Pending,
                Review = new OidcReviewDecision { Status = OidcScopeStatuses.Pending, RequestedBy = "SparkUsers/dev", RequestedAt = DateTime.UtcNow },
            });
        });
        return app;
    }

    private async Task DecideScopeAsync(OidcApplication app, OidcPortalAccess access, bool approve)
    {
        using var session = Store.OpenAsyncSession();
        await OidcScopeDecision.DecideAsync(
            Selected($"{app.Id}|{ApiName}.read"), approve, Substitute.For<IManager>(), session, access, Audit,
            Factory.GetService<OidcPortalMail>(), CancellationToken.None);
    }

    private async Task<OidcApplicationScope> PendingScopeAsync(OidcApplication app)
        => (await LoadAsync(app.Id!)).Scopes.Single(s => s.Name == ApiName + ".read");

    [Fact]
    public async Task The_api_owner_approves_a_pending_scope()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var app = await SeedPendingScopeApplicationAsync();

        await DecideScopeAsync(app, Access("SparkUsers/api-owner", administrator: false), approve: true);

        var scope = await PendingScopeAsync(app);
        scope.Status.Should().Be(OidcScopeStatuses.Approved);
        scope.Review!.Status.Should().Be(OidcScopeStatuses.Approved);
        scope.Review!.DecidedBy.Should().Be("SparkUsers/api-owner");
    }

    [Fact]
    public async Task The_api_owner_rejects_a_pending_scope()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var app = await SeedPendingScopeApplicationAsync();

        await DecideScopeAsync(app, Access("SparkUsers/api-owner", administrator: false), approve: false);

        (await PendingScopeAsync(app)).Status.Should().Be(OidcScopeStatuses.Rejected);
    }

    /// <summary>The requesting team cannot approve its own request: it stays Pending.</summary>
    [Fact]
    public async Task Someone_who_does_not_own_the_api_cannot_approve_its_scope()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var app = await SeedPendingScopeApplicationAsync();

        await DecideScopeAsync(app, Access("SparkUsers/dev", administrator: false), approve: true);

        (await PendingScopeAsync(app)).Status.Should().Be(OidcScopeStatuses.Pending);
    }

    // ---------- going Live (D4) ----------

    private sealed class Options : SparkIdentityProviderOptions
    {
        public Options(bool requireReview) => Apps.RequireReviewToGoLive = requireReview;
    }

    private async Task<OidcApplication> SeedDevelopmentApplicationAsync(string adminId)
    {
        var app = await SeedApplicationAsync(ClientId("client"));
        await UpdateAsync(app.Id!, a =>
        {
            a.Mode = OidcApplicationModes.Development;
            a.Members.Add(Member(adminId, OidcMemberRoles.Admin));
            a.Members.Add(Member("SparkUsers/team-dev", OidcMemberRoles.Developer));
        });
        return app;
    }

    private async Task SwitchToLiveAsync(OidcApplication app, OidcPortalAccess access, bool requireReview)
    {
        using var session = Store.OpenAsyncSession();
        var action = new SwitchToLiveAction(
            manager: Substitute.For<IManager>(), session: session, access: access, audit: Audit, options: new Options(requireReview));
        await action.ExecuteAsync(new CustomActionArgs
        {
            Parent = new PersistentObject { Id = app.Id, Name = nameof(OidcApplication), ObjectTypeId = Guid.NewGuid() },
        });
    }

    [Fact]
    public async Task Without_review_an_application_admin_switches_to_live_at_once()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");

        await SwitchToLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), requireReview: false);

        var stored = await LoadAsync(app.Id!);
        stored.Mode.Should().Be(OidcApplicationModes.Live);
        stored.GoLiveReview.Should().BeNull();
    }

    [Fact]
    public async Task With_review_an_application_admins_switch_becomes_a_pending_request()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");

        await SwitchToLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), requireReview: true);

        var stored = await LoadAsync(app.Id!);
        stored.Mode.Should().Be(OidcApplicationModes.Development, "the switch waits for an administrator");
        stored.GoLiveReview!.Status.Should().Be(OidcScopeStatuses.Pending);
        stored.GoLiveReview!.RequestedBy.Should().Be("SparkUsers/app-admin");
    }

    /// <summary>D3: mode switches are an Admin's; a team Developer changes nothing.</summary>
    [Fact]
    public async Task A_team_developer_cannot_switch_to_live()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");

        await SwitchToLiveAsync(app, Access("SparkUsers/team-dev", administrator: false), requireReview: false);

        var stored = await LoadAsync(app.Id!);
        stored.Mode.Should().Be(OidcApplicationModes.Development);
        stored.GoLiveReview.Should().BeNull();
    }

    private async Task DecideGoLiveAsync(OidcApplication app, OidcPortalAccess access, bool approve)
    {
        using var session = Store.OpenAsyncSession();
        await OidcGoLiveDecision.DecideAsync(
            Selected(app.Id!), approve, Substitute.For<IManager>(), session, access, Audit,
            Factory.GetService<OidcPortalMail>(), CancellationToken.None);
    }

    private async Task<OidcApplication> SeedGoLiveRequestAsync()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");
        await UpdateAsync(app.Id!, a => a.GoLiveReview = new OidcReviewDecision
        {
            Status = OidcScopeStatuses.Pending, RequestedBy = "SparkUsers/app-admin", RequestedAt = DateTime.UtcNow,
        });
        return app;
    }

    [Fact]
    public async Task An_administrator_approving_the_review_takes_the_application_live()
    {
        var app = await SeedGoLiveRequestAsync();

        await DecideGoLiveAsync(app, Access(Administrator, administrator: true), approve: true);

        var stored = await LoadAsync(app.Id!);
        stored.Mode.Should().Be(OidcApplicationModes.Live);
        stored.GoLiveReview!.Status.Should().Be(OidcScopeStatuses.Approved);
        stored.GoLiveReview!.DecidedBy.Should().Be(Administrator);
    }

    [Fact]
    public async Task An_administrator_rejecting_the_review_keeps_the_application_in_development()
    {
        var app = await SeedGoLiveRequestAsync();

        await DecideGoLiveAsync(app, Access(Administrator, administrator: true), approve: false);

        var stored = await LoadAsync(app.Id!);
        stored.Mode.Should().Be(OidcApplicationModes.Development);
        stored.GoLiveReview!.Status.Should().Be(OidcScopeStatuses.Rejected);
    }

    /// <summary>The application's own Admin is not the reviewer: the decision is an administrator's only.</summary>
    [Fact]
    public async Task An_application_admin_cannot_approve_its_own_go_live_request()
    {
        var app = await SeedGoLiveRequestAsync();

        await DecideGoLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), approve: true);

        var stored = await LoadAsync(app.Id!);
        stored.Mode.Should().Be(OidcApplicationModes.Development);
        stored.GoLiveReview!.Status.Should().Be(OidcScopeStatuses.Pending);
    }
}
