using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.IdentityProvider.CustomActions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The team actions on an application's page (<c>docs/identity_provider_platform_PRD.md</c> D3): inviting through the
/// retry form, resending a pending invitation, removing members without ever removing the last Admin, and switching
/// back to Development. Every one is an application Admin's; a team Developer changes nothing and is told so.
/// </summary>
public class OidcTeamActionsTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string AppAdmin = "SparkUsers/team-admin";
    private const string TeamDeveloper = "SparkUsers/team-developer";

    private static OidcPortalAccess Access(string userId)
    {
        var accessControl = Substitute.For<IAccessControl>();
        accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
            },
        };
        return new OidcPortalAccess(httpContextAccessor: http, accessControl: accessControl);
    }

    private OidcAudit Audit => Factory.GetService<OidcAudit>();

    private static OidcApplicationMember Member(string? userId, string role, string status = OidcMemberStatuses.Active, string? email = null) => new()
    {
        MemberId = Guid.NewGuid().ToString("N"),
        UserId = userId,
        Email = email,
        Role = role,
        Status = status,
        AcceptedAt = status == OidcMemberStatuses.Active ? DateTime.UtcNow : null,
        InvitationHash = status == OidcMemberStatuses.Invited ? "old-hash" : null,
    };

    private async Task<OidcApplication> SeedTeamAsync(params OidcApplicationMember[] extra)
    {
        var app = await SeedApplicationAsync(ClientId("team"));
        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<OidcApplication>(app.Id);
        stored.Members.Add(Member(AppAdmin, OidcMemberRoles.Admin));
        stored.Members.Add(Member(TeamDeveloper, OidcMemberRoles.Developer));
        stored.Members.AddRange(extra);
        await session.SaveChangesAsync();
        return stored;
    }

    private async Task<OidcApplication> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<OidcApplication>(id);
    }

    private static PersistentObject Parent(string id) => new() { Id = id, Name = nameof(OidcApplication), ObjectTypeId = Guid.NewGuid() };

    private static CustomActionArgs Rows(string applicationId, params string[] memberIds) => new()
    {
        QueryParent = Parent(applicationId),
        SelectedItems = [.. memberIds.Select(id => new QueryResultItem { Id = id, Values = [] })],
    };

    private static IManager Manager()
    {
        var manager = Substitute.For<IManager>();
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.Arg<string>());
        return manager;
    }

    private static void Notified(IManager manager, string key, NotificationKind kind)
        => manager.Client.Received().Notify(key, kind);

    // ---------- invite ----------

    private static IManager Answering(string option, string? email, string? role)
    {
        var manager = Manager();
        PersistentObject Form(string? emailValue, string? roleValue) => new()
        {
            Name = "OidcInvitation",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [new PersistentObjectAttribute { Name = "Email", Value = emailValue }, new PersistentObjectAttribute { Name = "Role", Value = roleValue }],
        };
        // The blank prompt the action fills in, and the form as the user sent it back: two objects, as over the wire.
        manager.GetPersistentObjectAsync("OidcInvitation", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Form(null, null));
        manager.Retry.Result.Returns(new RetryResult { Option = option, PersistentObject = Form(email, role) });
        return manager;
    }

    private async Task InviteAsync(IManager manager, string applicationId, string userId)
    {
        using var session = Store.OpenAsyncSession();
        await new InviteMemberAction(
            manager: manager, session: session, access: Access(userId), invitations: Factory.GetService<OidcInvitations>(),
            audit: Audit, teamMail: Factory.GetService<OidcTeamMail>())
            .ExecuteAsync(new CustomActionArgs { Parent = Parent(applicationId) });
    }

    [Fact]
    public async Task An_admin_invites_through_the_form_and_the_invitation_is_pending()
    {
        var app = await SeedTeamAsync();
        var manager = Answering("identityProvider.invite", $"  {UserEmail("new")}  ", OidcMemberRoles.Tester);

        await InviteAsync(manager, app.Id!, AppAdmin);

        var invited = (await LoadAsync(app.Id!)).Members.Should().ContainSingle(m => m.Status == OidcMemberStatuses.Invited).Which;
        invited.Email.Should().Be(UserEmail("new"));
        invited.Role.Should().Be(OidcMemberRoles.Tester);
        invited.InvitedBy.Should().Be(AppAdmin);
        Notified(manager, "identityProvider.invitationSent", NotificationKind.Success);
        manager.Client.Received().RefreshQuery(Arg.Any<string>());
    }

    [Fact]
    public async Task The_form_opens_with_developer_as_the_role()
    {
        var app = await SeedTeamAsync();
        var manager = Answering(RetryResult.CancelOption, null, null);

        await InviteAsync(manager, app.Id!, AppAdmin);

        var prompt = await manager.GetPersistentObjectAsync("OidcInvitation");
        prompt["Role"].Value.Should().Be(OidcMemberRoles.Developer);
        (await LoadAsync(app.Id!)).Members.Should().HaveCount(2, "a cancelled form invites nobody");
        manager.Client.DidNotReceive().Notify(Arg.Any<string>(), Arg.Any<NotificationKind>());
    }

    [Theory]
    [InlineData("not-an-address", OidcMemberRoles.Developer)]
    [InlineData("", OidcMemberRoles.Developer)]
    [InlineData("someone@test.local", "Owner")]
    public async Task An_invalid_address_or_role_is_refused(string email, string role)
    {
        var app = await SeedTeamAsync();
        var manager = Answering("identityProvider.invite", email, role);

        await InviteAsync(manager, app.Id!, AppAdmin);

        (await LoadAsync(app.Id!)).Members.Should().HaveCount(2);
        Notified(manager, "identityProvider.inviteInvalid", NotificationKind.Error);
    }

    [Fact]
    public async Task A_team_developer_cannot_invite_and_an_unknown_application_is_ignored()
    {
        var app = await SeedTeamAsync();
        var manager = Answering("identityProvider.invite", UserEmail("new"), OidcMemberRoles.Tester);

        await InviteAsync(manager, app.Id!, TeamDeveloper);
        await InviteAsync(manager, "OidcApplications/does-not-exist", AppAdmin);

        (await LoadAsync(app.Id!)).Members.Should().HaveCount(2);
        Notified(manager, "identityProvider.adminOnly", NotificationKind.Error);
        manager.Retry.DidNotReceiveWithAnyArgs().Action(default!, default!, default, default, default);
    }

    // ---------- resend ----------

    private async Task ResendAsync(IManager manager, CustomActionArgs args, string userId)
    {
        using var session = Store.OpenAsyncSession();
        await new ResendInvitationAction(
            manager: manager, session: session, access: Access(userId), invitations: Factory.GetService<OidcInvitations>(),
            audit: Audit, teamMail: Factory.GetService<OidcTeamMail>())
            .ExecuteAsync(args);
    }

    [Fact]
    public async Task Resending_replaces_the_pending_invitation_with_a_new_link_and_skips_active_members()
    {
        var pending = Member(null, OidcMemberRoles.Tester, OidcMemberStatuses.Invited, UserEmail("pending"));
        var app = await SeedTeamAsync(pending);
        var developer = app.Members.Single(m => m.UserId == TeamDeveloper);
        var manager = Manager();

        await ResendAsync(manager, Rows(app.Id!, pending.MemberId!, developer.MemberId!), AppAdmin);

        var members = (await LoadAsync(app.Id!)).Members;
        members.Single(m => m.MemberId == pending.MemberId).Status.Should().Be(OidcMemberStatuses.Revoked);
        var fresh = members.Should().ContainSingle(m => m.Status == OidcMemberStatuses.Invited).Which;
        fresh.Email.Should().Be(UserEmail("pending"));
        fresh.Role.Should().Be(OidcMemberRoles.Tester);
        fresh.InvitationHash.Should().NotBe("old-hash");
        members.Single(m => m.UserId == TeamDeveloper).Status.Should().Be(OidcMemberStatuses.Active);
        Notified(manager, "identityProvider.invitationSent", NotificationKind.Success);
    }

    [Fact]
    public async Task A_team_developer_cannot_resend_and_a_row_without_its_application_is_ignored()
    {
        var pending = Member(null, OidcMemberRoles.Tester, OidcMemberStatuses.Invited, UserEmail("pending"));
        var app = await SeedTeamAsync(pending);
        var manager = Manager();

        await ResendAsync(manager, Rows(app.Id!, pending.MemberId!), TeamDeveloper);
        await ResendAsync(manager, new CustomActionArgs { SelectedItems = [] }, AppAdmin);

        (await LoadAsync(app.Id!)).Members.Single(m => m.MemberId == pending.MemberId).InvitationHash.Should().Be("old-hash");
        Notified(manager, "identityProvider.adminOnly", NotificationKind.Error);
    }

    // ---------- remove ----------

    private async Task RemoveAsync(IManager manager, CustomActionArgs args, string userId)
    {
        using var session = Store.OpenAsyncSession();
        await new RemoveMemberAction(manager: manager, session: session, access: Access(userId), audit: Audit).ExecuteAsync(args);
    }

    [Fact]
    public async Task Removing_revokes_the_member_and_the_pending_invitation()
    {
        var pending = Member(null, OidcMemberRoles.Tester, OidcMemberStatuses.Invited, UserEmail("pending"));
        var app = await SeedTeamAsync(pending);
        var developer = app.Members.Single(m => m.UserId == TeamDeveloper);
        var manager = Manager();

        await RemoveAsync(manager, Rows(app.Id!, developer.MemberId!, pending.MemberId!), AppAdmin);

        var members = (await LoadAsync(app.Id!)).Members;
        members.Single(m => m.MemberId == developer.MemberId).Status.Should().Be(OidcMemberStatuses.Revoked);
        var revoked = members.Single(m => m.MemberId == pending.MemberId);
        revoked.Status.Should().Be(OidcMemberStatuses.Revoked);
        revoked.InvitationHash.Should().BeNull();
        members.Single(m => m.UserId == AppAdmin).Status.Should().Be(OidcMemberStatuses.Active);
        manager.Client.Received().RefreshQuery(Arg.Any<string>());
    }

    [Fact]
    public async Task The_last_active_admin_is_never_removed()
    {
        var app = await SeedTeamAsync();
        var admin = app.Members.Single(m => m.UserId == AppAdmin);
        var manager = Manager();

        await RemoveAsync(manager, Rows(app.Id!, admin.MemberId!), AppAdmin);

        (await LoadAsync(app.Id!)).Members.Single(m => m.UserId == AppAdmin).Status.Should().Be(OidcMemberStatuses.Active);
        Notified(manager, "identityProvider.lastAdmin", NotificationKind.Error);
    }

    [Fact]
    public async Task A_second_admin_can_be_removed_and_a_team_developer_removes_nobody()
    {
        var second = Member("SparkUsers/second-admin", OidcMemberRoles.Admin);
        var app = await SeedTeamAsync(second);
        var manager = Manager();

        await RemoveAsync(manager, Rows(app.Id!, second.MemberId!), TeamDeveloper);
        (await LoadAsync(app.Id!)).Members.Single(m => m.MemberId == second.MemberId).Status.Should().Be(OidcMemberStatuses.Active);
        Notified(manager, "identityProvider.adminOnly", NotificationKind.Error);

        await RemoveAsync(manager, Rows(app.Id!, second.MemberId!), AppAdmin);
        (await LoadAsync(app.Id!)).Members.Single(m => m.MemberId == second.MemberId).Status.Should().Be(OidcMemberStatuses.Revoked);
    }

    // ---------- back to Development ----------

    private async Task SwitchToDevelopmentAsync(IManager manager, string applicationId, string userId)
    {
        using var session = Store.OpenAsyncSession();
        await new SwitchToDevelopmentAction(manager: manager, session: session, access: Access(userId), audit: Audit)
            .ExecuteAsync(new CustomActionArgs { Parent = Parent(applicationId) });
    }

    private async Task SetModeAsync(string id, string mode)
    {
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<OidcApplication>(id)).Mode = mode;
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task An_admin_takes_a_live_application_back_to_development()
    {
        var app = await SeedTeamAsync();
        await SetModeAsync(app.Id!, OidcApplicationModes.Live);
        var manager = Manager();

        await SwitchToDevelopmentAsync(manager, app.Id!, AppAdmin);

        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Development);
        manager.Client.Received().RefreshAttribute(Arg.Any<PersistentObject>(), nameof(OidcApplication.Mode));
    }

    [Fact]
    public async Task A_team_developer_cannot_switch_back_and_a_development_application_stays_as_it_is()
    {
        var app = await SeedTeamAsync();
        await SetModeAsync(app.Id!, OidcApplicationModes.Live);
        var manager = Manager();

        await SwitchToDevelopmentAsync(manager, app.Id!, TeamDeveloper);
        (await LoadAsync(app.Id!)).Mode.Should().Be(OidcApplicationModes.Live);
        Notified(manager, "identityProvider.adminOnly", NotificationKind.Error);

        await SetModeAsync(app.Id!, OidcApplicationModes.Development);
        var quiet = Manager();
        await SwitchToDevelopmentAsync(quiet, app.Id!, AppAdmin);
        quiet.Client.DidNotReceiveWithAnyArgs().RefreshAttribute(default!, default!);
    }
}
