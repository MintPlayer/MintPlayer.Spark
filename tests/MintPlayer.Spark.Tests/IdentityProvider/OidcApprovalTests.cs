using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.CustomActions;
using MintPlayer.Spark.IdentityProvider.Interceptors;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The decisions the portal queues for someone else (<c>docs/identity_provider_platform_PRD.md</c> D2, D4), as
/// approval requests since commerce D26: developer requests (<c>identity-provider.developer</c>), API scopes asked
/// for by an application whose team does not own the API (<c>identity-provider.scope</c>), and going Live under
/// <c>Apps:RequireReviewToGoLive</c> (<c>identity-provider.go-live</c>).
/// <para>
/// Decisions go through <see cref="ISparkApprovals"/> as a given user (<see cref="AsAsync{T}"/>), so the kinds'
/// real deciders and handlers run: what is asserted is the state each decision leaves on the IdP's documents and on
/// the request, and who may make it. <see cref="ApprovalHost"/> answers <c>ManageAll/IdentityProvider</c> for
/// <see cref="Administrator"/> only.
/// </para>
/// </summary>
public class OidcApprovalTests(OidcApprovalTests.ApprovalHost host) : OidcTestHost(host), IClassFixture<OidcApprovalTests.ApprovalHost>
{
    private const string Administrator = "SparkUsers/idp-admin";

    /// <summary>The provider, with <c>ManageAll/*</c> granted to <see cref="Administrator"/> alone and every other right to everyone.</summary>
    public sealed class ApprovalHost : OidcSharedHost
    {
        // HttpContextAccessor keeps the current context in a static AsyncLocal: this instance sees the caller's.
        private static readonly HttpContextAccessor Current = new();

        protected override void ConfigureServices(IServiceCollection services)
            => services.UseSparkTestAccessControl(SparkTestAccessControl.Matching(resource =>
                !resource.StartsWith("ManageAll/", StringComparison.OrdinalIgnoreCase)
                || Current.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) == Administrator));
    }

    private static ClaimsPrincipal Principal(string userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static IHttpContextAccessor As(string userId) => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext { User = Principal(userId) },
    };

    /// <summary>The caller's portal access; <paramref name="administrator"/> answers <c>ManageAll/IdentityProvider</c>.</summary>
    private static OidcPortalAccess Access(string userId, bool administrator)
    {
        var accessControl = Substitute.For<IAccessControl>();
        accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(administrator);
        return new OidcPortalAccess(httpContextAccessor: As(userId), accessControl: accessControl);
    }

    /// <summary>Runs <paramref name="act"/> in a request scope of its own, signed in as <paramref name="userId"/>.</summary>
    private async Task<T> AsAsync<T>(string userId, Func<ISparkApprovals, Task<T>> act)
    {
        using var scope = Factory.GetService<IServiceScopeFactory>().CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext { User = Principal(userId), RequestServices = scope.ServiceProvider };
        try
        {
            return await act(scope.ServiceProvider.GetRequiredService<ISparkApprovals>());
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private Task<ApprovalResult> DecideAsync(string userId, string requestId, bool approve, string? reason = null)
        => AsAsync(userId, approvals => approvals.DecideAsync(requestId, approve ? ApprovalDecision.Approve : ApprovalDecision.Reject, reason));

    private async Task<ApprovalRequest> RequestAsync(string requestId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<ApprovalRequest>(requestId))!;
    }

    /// <summary>The pending requests of <typeparamref name="TKind"/> about <paramref name="subject"/> (server code: no visibility filter).</summary>
    private async Task<IReadOnlyList<ApprovalRequestInfo>> PendingAsync<TKind>(ApprovalSubject subject) where TKind : IApprovalKind
    {
        using var scope = Factory.GetService<IServiceScopeFactory>().CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISparkApprovals>().GetPendingAsync<TKind>(subject);
    }

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

    /// <summary>A user with a <c>Requested</c> status and its pending request, as the portal leaves them; returns (user, request).</summary>
    private async Task<(string UserId, string RequestId)> SeedDeveloperRequestAsync(string localPart)
    {
        var user = await SeedUserAsync(UserEmail(localPart));
        using (var session = Store.OpenAsyncSession())
        {
            OidcDevelopers.Set(session, user.Id!, new OidcDeveloper { Status = OidcDeveloperStatuses.Requested, TermsVersion = 1, RequestedAt = DateTime.UtcNow });
            await session.SaveChangesAsync();
        }
        var submitted = await AsAsync(user.Id!, approvals =>
            approvals.SubmitAsync<OidcDeveloperApprovalKind>(OidcApprovalRequests.Developer(user.Id!, user.UserName, user.Email, 1)));
        return (user.Id!, submitted.RequestId);
    }

    private async Task SetDeveloperStatusAsync(string userId, string status)
    {
        using var session = Store.OpenAsyncSession();
        OidcDevelopers.Set(session, userId, new OidcDeveloper { Status = status, TermsVersion = 1, RequestedAt = DateTime.UtcNow });
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Approving_a_developer_request_makes_the_user_an_active_developer()
    {
        var (userId, requestId) = await SeedDeveloperRequestAsync("dev");

        var result = await DecideAsync(Administrator, requestId, approve: true);

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        var developer = await Developers.GetAsync(userId);
        developer!.Status.Should().Be(OidcDeveloperStatuses.Approved);
        developer.DecidedBy.Should().Be(Administrator);
        developer.DecidedAt.HasValue.Should().BeTrue();
        Developers.IsActive(developer).Should().BeTrue();
        (await RequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Approved);
    }

    /// <summary>F3: the rejection's reason is kept on the request and shown to the user as the developer note.</summary>
    [Fact]
    public async Task Rejecting_a_developer_request_records_the_reason_and_leaves_the_user_without_portal_access()
    {
        var (userId, requestId) = await SeedDeveloperRequestAsync("dev");

        var result = await DecideAsync(Administrator, requestId, approve: false, reason: "Use your company account.");

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        var developer = await Developers.GetAsync(userId);
        developer!.Status.Should().Be(OidcDeveloperStatuses.Rejected);
        developer.Note.Should().Be("Use your company account.");
        Developers.IsActive(developer).Should().BeFalse();
        (await RequestAsync(requestId)).Reason.Should().Be("Use your company account.");
    }

    [Fact]
    public async Task A_rejection_without_a_reason_is_refused()
    {
        var (userId, requestId) = await SeedDeveloperRequestAsync("dev");

        var result = await DecideAsync(Administrator, requestId, approve: false, reason: "  ");

        result.Outcome.Should().Be(ApprovalOutcome.ReasonRequired);
        (await Developers.GetAsync(userId))!.Status.Should().Be(OidcDeveloperStatuses.Requested);
    }

    /// <summary>F3: an administrator can take an approval back; the status becomes <c>Revoked</c>, with the reason, and asking again is refused.</summary>
    [Fact]
    public async Task Revoking_an_approved_developer_produces_revoked_with_the_reason()
    {
        var (userId, requestId) = await SeedDeveloperRequestAsync("dev");
        await DecideAsync(Administrator, requestId, approve: true);

        var result = await AsAsync(Administrator, approvals => approvals.RevokeAsync(requestId, "Leaked a client secret."));

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        var developer = await Developers.GetAsync(userId);
        developer!.Status.Should().Be(OidcDeveloperStatuses.Revoked);
        developer.Note.Should().Be("Leaked a client secret.");
        Developers.IsActive(developer).Should().BeFalse();
        (await RequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Revoked);
    }

    /// <summary>The live gate: someone who is not an identity-provider administrator gets the uniform refusal.</summary>
    [Fact]
    public async Task Someone_who_is_not_an_administrator_cannot_decide_a_developer_request()
    {
        var (userId, requestId) = await SeedDeveloperRequestAsync("dev");

        var result = await DecideAsync("SparkUsers/someone", requestId, approve: true);

        result.Outcome.Should().Be(ApprovalOutcome.NotFound);
        (await Developers.GetAsync(userId))!.Status.Should().Be(OidcDeveloperStatuses.Requested);
    }

    /// <summary>
    /// Only a status still <c>Requested</c> is decided: a request whose user was rejected or revoked some other way
    /// cannot approve them. The request goes stale instead.
    /// </summary>
    [Theory]
    [InlineData(OidcDeveloperStatuses.Rejected)]
    [InlineData(OidcDeveloperStatuses.Revoked)]
    public async Task Approving_never_overturns_a_status_that_is_no_longer_requested(string status)
    {
        var (userId, requestId) = await SeedDeveloperRequestAsync("dev");
        await SetDeveloperStatusAsync(userId, status);

        var result = await DecideAsync(Administrator, requestId, approve: true);

        result.Outcome.Should().Be(ApprovalOutcome.Stale);
        (await Developers.GetAsync(userId))!.Status.Should().Be(status);
    }

    /// <summary>The request half over HTTP: with <c>RequireApproval</c> (the default) a request waits as an approval request, and the approval activates it.</summary>
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

        var pending = await PendingAsync<OidcDeveloperApprovalKind>(OidcApprovalRequests.DeveloperSubject(user.Id!));
        pending.Should().ContainSingle().Which.RequestedBy.Should().Be(user.Id);

        // Asking again while it is pending finds the same request rather than raising a second one.
        (await portal.PostJsonAsync("/spark/identity-provider/developer", new { termsVersion = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PendingAsync<OidcDeveloperApprovalKind>(OidcApprovalRequests.DeveloperSubject(user.Id!))).Should().ContainSingle();

        (await DecideAsync(Administrator, pending[0].Id, approve: true)).Outcome.Should().Be(ApprovalOutcome.Done);

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
        (await PendingAsync<OidcDeveloperApprovalKind>(OidcApprovalRequests.DeveloperSubject(user.Id!))).Should().BeEmpty();
    }

    /// <summary>An administrator's revocation is not undone by asking again.</summary>
    [Fact]
    public async Task A_revoked_developer_cannot_request_again()
    {
        var email = UserEmail("dev");
        var user = await SeedUserAsync(email);
        await SetDeveloperStatusAsync(user.Id!, OidcDeveloperStatuses.Revoked);
        var portal = await OidcPortalClient.SignInAsync(Client, email, Password);

        var response = await portal.PostJsonAsync("/spark/identity-provider/developer", new { termsVersion = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Developers.GetAsync(user.Id!))!.Status.Should().Be(OidcDeveloperStatuses.Revoked);
        (await PendingAsync<OidcDeveloperApprovalKind>(OidcApprovalRequests.DeveloperSubject(user.Id!))).Should().BeEmpty();
    }

    // ---------- cross-owner API scopes (D4) ----------

    /// <summary>A resource name unique to the case; an API scope is "{resource}.{name}".</summary>
    private string ApiName => "billing-" + Scope;

    private string ApiScope => ApiName + ".read";

    private async Task SeedApiAsync(string owner, bool autoApprove = false, string? name = null)
    {
        _ = Factory;
        name ??= ApiName;
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new OidcResource
        {
            Id = OidcScopeCatalog.ResourceId(name),
            Kind = OidcResourceKinds.Api,
            Name = name,
            DisplayName = TranslatedString.Of("en", name),
            Enabled = true,
            Owners = [owner],
            AutoApprove = autoApprove,
            Scopes = [new OidcApiScope { Name = name + ".read", DisplayName = TranslatedString.Of("en", "read") }],
        });
        await session.SaveChangesAsync();
    }

    private async Task<List<OidcApplicationScope>> MarkAsync(OidcApplication app, params string[] scopes)
    {
        var added = scopes.Select(s => new OidcApplicationScope { Name = s }).ToList();
        app.Scopes.AddRange(added);
        using var session = Store.OpenAsyncSession();
        await OidcApprovalRequests.MarkCrossOwnerScopesAsync(session, app, added);
        return added;
    }

    [Fact]
    public async Task An_api_scope_the_team_does_not_own_starts_pending()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var app = new OidcApplication { ClientId = ClientId("client"), Members = [Member("SparkUsers/dev", OidcMemberRoles.Admin)] };

        var added = await MarkAsync(app, "openid", ApiScope);

        added[0].Status.Should().Be(OidcScopeStatuses.Approved, "identity scopes need nobody's approval");
        added[1].Status.Should().Be(OidcScopeStatuses.Pending);
    }

    [Fact]
    public async Task An_api_scope_of_the_teams_own_api_is_approved_at_once()
    {
        await SeedApiAsync(owner: "SparkUsers/dev");
        var app = new OidcApplication { ClientId = ClientId("client"), Members = [Member("SparkUsers/dev", OidcMemberRoles.Developer)] };

        var added = await MarkAsync(app, ApiScope);

        added[0].Status.Should().Be(OidcScopeStatuses.Approved);
    }

    [Fact]
    public async Task An_api_scope_of_an_auto_approve_api_is_approved_at_once()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner", autoApprove: true);
        var app = new OidcApplication { ClientId = ClientId("client"), Members = [Member("SparkUsers/dev", OidcMemberRoles.Admin)] };

        var added = await MarkAsync(app, ApiScope);

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

    /// <summary>
    /// One save can raise several requests, one per cross-owner scope, each for that API's owners, visible to the
    /// application's team; they commit with the application's own session.
    /// </summary>
    [Fact]
    public async Task Each_cross_owner_scope_raises_its_own_request_for_that_apis_owners()
    {
        var otherApi = "shipping-" + Scope;
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        await SeedApiAsync(owner: "SparkUsers/other-owner", name: otherApi);
        var seeded = await SeedApplicationAsync(ClientId("client"));
        await UpdateAsync(seeded.Id!, a =>
        {
            a.Members.Add(Member("SparkUsers/dev", OidcMemberRoles.Admin));
            a.Members.Add(Member("SparkUsers/tester", OidcMemberRoles.Tester));
        });

        using (var scope = Factory.GetService<IServiceScopeFactory>().CreateScope())
        using (var session = Store.OpenAsyncSession())
        {
            var app = await session.LoadAsync<OidcApplication>(seeded.Id);
            List<OidcApplicationScope> added = [new() { Name = ApiScope }, new() { Name = otherApi + ".read" }];
            app.Scopes.AddRange(added);
            await OidcApprovalRequests.RaiseScopeRequestsAsync(scope.ServiceProvider.GetRequiredService<ISparkApprovals>(), session, app, added, "SparkUsers/dev");
            await session.SaveChangesAsync();
        }

        var billing = (await PendingAsync<OidcScopeApprovalKind>(OidcApprovalRequests.ScopeSubject(seeded.Id!, ApiScope))).Should().ContainSingle().Which;
        var shipping = (await PendingAsync<OidcScopeApprovalKind>(OidcApprovalRequests.ScopeSubject(seeded.Id!, otherApi + ".read"))).Should().ContainSingle().Which;
        billing.RequestedBy.Should().Be("SparkUsers/dev");
        var billingRequest = await RequestAsync(billing.Id);
        billingRequest.DeciderIds.Should().BeEquivalentTo(["SparkUsers/api-owner"]);
        // The team's Admins and Developers follow it; a Tester does not.
        billingRequest.VisibleTo.Should().Equal("SparkUsers/dev");
        (await RequestAsync(shipping.Id)).DeciderIds.Should().BeEquivalentTo(["SparkUsers/other-owner"]);
        (await LoadAsync(seeded.Id!)).Scopes.Where(s => s.Status == OidcScopeStatuses.Pending).Select(s => s.Name)
            .Should().BeEquivalentTo([ApiScope, otherApi + ".read"]);
    }

    /// <summary>An application with a pending cross-owner scope and its request, as the save leaves them; returns (app, request).</summary>
    private async Task<(OidcApplication App, string RequestId)> SeedPendingScopeAsync()
    {
        var seeded = await SeedApplicationAsync(ClientId("client"));
        await UpdateAsync(seeded.Id!, a =>
        {
            a.Members.Add(Member("SparkUsers/dev", OidcMemberRoles.Admin));
            a.Members.Add(Member("SparkUsers/team-dev", OidcMemberRoles.Developer));
            a.Scopes.Add(new OidcApplicationScope { Name = ApiScope, Status = OidcScopeStatuses.Pending });
        });
        var app = await LoadAsync(seeded.Id!);
        var submitted = await AsAsync("SparkUsers/dev", approvals =>
            approvals.SubmitAsync<OidcScopeApprovalKind>(OidcApprovalRequests.Scope(app, ApiScope, ApiName, "SparkUsers/dev")));
        return (app, submitted.RequestId);
    }

    private async Task<OidcApplicationScope> PendingScopeAsync(OidcApplication app)
        => (await LoadAsync(app.Id!)).Scopes.Single(s => s.Name == ApiScope);

    [Fact]
    public async Task The_api_owner_approves_a_pending_scope()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var (app, requestId) = await SeedPendingScopeAsync();

        var result = await DecideAsync("SparkUsers/api-owner", requestId, approve: true);

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        (await PendingScopeAsync(app)).Status.Should().Be(OidcScopeStatuses.Approved);
        var request = await RequestAsync(requestId);
        request.Status.Should().Be(ApprovalStatuses.Approved);
        request.DecidedBy.Should().Be("SparkUsers/api-owner");
    }

    /// <summary>F3: the rejection's reason stays on the request; the row is <c>Rejected</c>.</summary>
    [Fact]
    public async Task The_api_owner_rejects_a_pending_scope_with_a_reason()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var (app, requestId) = await SeedPendingScopeAsync();

        var result = await DecideAsync("SparkUsers/api-owner", requestId, approve: false, reason: "Billing data is internal.");

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        (await PendingScopeAsync(app)).Status.Should().Be(OidcScopeStatuses.Rejected);
        (await RequestAsync(requestId)).Reason.Should().Be("Billing data is internal.");
    }

    [Fact]
    public async Task An_administrator_approves_a_pending_scope_of_an_api_they_do_not_own()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var (app, requestId) = await SeedPendingScopeAsync();

        (await DecideAsync(Administrator, requestId, approve: true)).Outcome.Should().Be(ApprovalOutcome.Done);

        (await PendingScopeAsync(app)).Status.Should().Be(OidcScopeStatuses.Approved);
    }

    /// <summary>The requesting team cannot decide its own request, neither the requester nor a teammate: it stays Pending.</summary>
    [Theory]
    [InlineData("SparkUsers/dev")]
    [InlineData("SparkUsers/team-dev")]
    public async Task Someone_who_does_not_own_the_api_cannot_approve_its_scope(string caller)
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var (app, requestId) = await SeedPendingScopeAsync();

        var result = await DecideAsync(caller, requestId, approve: true);

        result.Outcome.Should().Be(ApprovalOutcome.NotFound);
        (await PendingScopeAsync(app)).Status.Should().Be(OidcScopeStatuses.Pending);
        (await RequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Pending);
    }

    // ---------- F1: the scope status is the server's ----------

    private OidcApplicationInterceptors Interceptors(IServiceScope scope, bool administrator) => new(
        corsOrigins: new OidcCorsOrigins(),
        accessControl: administrator ? SparkTestAccessControl.AllowAll() : SparkTestAccessControl.DenyAll(),
        options: Factory.GetService<SparkIdentityProviderOptions>(),
        audit: Audit,
        approvals: scope.ServiceProvider.GetRequiredService<ISparkApprovals>());

    /// <summary>Saves <paramref name="change"/> on the stored application through the before-save interceptor, as <paramref name="userId"/>.</summary>
    private async Task SaveThroughInterceptorAsync(string applicationId, string userId, Action<OidcApplication> change, bool administrator = false)
    {
        var before = await LoadAsync(applicationId);
        using var scope = Factory.GetService<IServiceScopeFactory>().CreateScope();
        using var session = Store.OpenAsyncSession();
        var entity = await session.LoadAsync<OidcApplication>(applicationId);
        change(entity);
        await Interceptors(scope, administrator).OnBeforeSaveAsync(entity, new SaveContext
        {
            EntityType = typeof(OidcApplication),
            Operation = PersistentObjectOperation.Save,
            PersistentObject = new PersistentObject { Id = applicationId, Name = nameof(OidcApplication), ObjectTypeId = Guid.NewGuid() },
            Entity = entity,
            Before = before,
            User = Principal(userId),
            Session = session,
        });
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// F1 (security; red before the fix): a team Developer holds Edit on the scope rows, and the interceptor reset
    /// only the statuses of <em>added</em> scopes. Posting <c>"Approved"</c> on the application's own pending or
    /// rejected cross-owner scope served it to everyone. A kept scope now keeps its stored status.
    /// </summary>
    [Theory]
    [InlineData(OidcScopeStatuses.Pending)]
    [InlineData(OidcScopeStatuses.Rejected)]
    public async Task A_team_developer_cannot_approve_its_own_cross_owner_scope_by_saving_it(string stored)
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var app = await SeedApplicationAsync(ClientId("client"));
        await UpdateAsync(app.Id!, a =>
        {
            a.Members.Add(Member("SparkUsers/team-dev", OidcMemberRoles.Developer));
            a.Scopes.Add(new OidcApplicationScope { Name = ApiScope, Status = stored });
        });

        await SaveThroughInterceptorAsync(app.Id!, "SparkUsers/team-dev",
            a => a.Scopes.Single(s => s.Name == ApiScope).Status = OidcScopeStatuses.Approved);

        var saved = await LoadAsync(app.Id!);
        saved.Scopes.Single(s => s.Name == ApiScope).Status.Should().Be(stored);
        OidcApplicationAccess.AvailableScopes(saved, "SparkUsers/outsider", ["openid", ApiScope])
            .Should().Equal("openid");
    }

    /// <summary>F1, the model half: the scope row's status is read-only, so the save pipeline keeps the stored value.</summary>
    [Fact]
    public void The_scope_status_is_read_only_in_the_model()
    {
        var definition = Factory.GetService<IModelLoader>().GetEntityTypeByClrType(typeof(OidcApplicationScope).FullName!);

        definition!.Attributes.Single(a => a.Name == nameof(OidcApplicationScope.Status)).IsReadOnly.Should().BeTrue();
    }

    /// <summary>Removing a pending scope from the application withdraws its request in the same save.</summary>
    [Fact]
    public async Task Removing_a_pending_scope_withdraws_its_request()
    {
        await SeedApiAsync(owner: "SparkUsers/api-owner");
        var (app, requestId) = await SeedPendingScopeAsync();

        await SaveThroughInterceptorAsync(app.Id!, "SparkUsers/dev", a => a.Scopes.RemoveAll(s => s.Name == ApiScope));

        (await RequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Withdrawn);
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
        using var scope = Factory.GetService<IServiceScopeFactory>().CreateScope();
        using var session = Store.OpenAsyncSession();
        var action = new SwitchToLiveAction(
            manager: Substitute.For<IManager>(), session: session, access: access, audit: Audit, options: new Options(requireReview),
            approvals: scope.ServiceProvider.GetRequiredService<ISparkApprovals>());
        await action.ExecuteAsync(new CustomActionArgs
        {
            Parent = new PersistentObject { Id = app.Id, Name = nameof(OidcApplication), ObjectTypeId = Guid.NewGuid() },
        });
    }

    private Task<IReadOnlyList<ApprovalRequestInfo>> GoLiveRequestsAsync(OidcApplication app)
        => PendingAsync<OidcGoLiveApprovalKind>(OidcApprovalRequests.ApplicationSubject(app.Id!));

    [Fact]
    public async Task Without_review_an_application_admin_switches_to_live_at_once()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");

        await SwitchToLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), requireReview: false);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Live);
        (await GoLiveRequestsAsync(app)).Should().BeEmpty();
    }

    [Fact]
    public async Task With_review_an_application_admins_switch_becomes_a_pending_request()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");

        await SwitchToLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), requireReview: true);
        // A second click finds the pending request: no second one.
        await SwitchToLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), requireReview: true);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Development, "the switch waits for an administrator");
        (await GoLiveRequestsAsync(app)).Should().ContainSingle().Which.RequestedBy.Should().Be("SparkUsers/app-admin");
    }

    /// <summary>D3: mode switches are an Admin's; a team Developer changes nothing.</summary>
    [Fact]
    public async Task A_team_developer_cannot_switch_to_live()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");

        await SwitchToLiveAsync(app, Access("SparkUsers/team-dev", administrator: false), requireReview: true);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Development);
        (await GoLiveRequestsAsync(app)).Should().BeEmpty();
    }

    private async Task<(OidcApplication App, string RequestId)> SeedGoLiveRequestAsync()
    {
        var app = await SeedDevelopmentApplicationAsync("SparkUsers/app-admin");
        await SwitchToLiveAsync(app, Access("SparkUsers/app-admin", administrator: false), requireReview: true);
        return (app, (await GoLiveRequestsAsync(app)).Single().Id);
    }

    [Fact]
    public async Task An_administrator_approving_the_review_takes_the_application_live()
    {
        var (app, requestId) = await SeedGoLiveRequestAsync();

        (await DecideAsync(Administrator, requestId, approve: true)).Outcome.Should().Be(ApprovalOutcome.Done);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Live);
        var request = await RequestAsync(requestId);
        request.Status.Should().Be(ApprovalStatuses.Approved);
        request.DecidedBy.Should().Be(Administrator);
    }

    [Fact]
    public async Task An_administrator_rejecting_the_review_keeps_the_application_in_development_with_the_reason()
    {
        var (app, requestId) = await SeedGoLiveRequestAsync();

        (await DecideAsync(Administrator, requestId, approve: false, reason: "Add a privacy policy first.")).Outcome.Should().Be(ApprovalOutcome.Done);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Development);
        var request = await RequestAsync(requestId);
        request.Status.Should().Be(ApprovalStatuses.Rejected);
        request.Reason.Should().Be("Add a privacy policy first.");
    }

    /// <summary>The application's own Admin is not the reviewer (and is the requester): the decision is an administrator's only.</summary>
    [Theory]
    [InlineData("SparkUsers/app-admin")]
    [InlineData("SparkUsers/team-dev")]
    public async Task The_applications_own_team_cannot_approve_its_go_live_request(string caller)
    {
        var (app, requestId) = await SeedGoLiveRequestAsync();

        (await DecideAsync(caller, requestId, approve: true)).Outcome.Should().Be(ApprovalOutcome.NotFound);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Development);
        (await RequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Pending);
    }
}
