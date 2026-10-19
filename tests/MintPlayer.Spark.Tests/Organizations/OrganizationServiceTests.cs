using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Migrations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// X11 (<c>docs/seo_analyzer_PRD.md</c> D18, D21–D23, D26) through <see cref="ISparkOrganizations"/> on a
/// booted host: personal organizations, creation, invitations with their mailed single-use links, join
/// requests decided in-app through Approvals (D26), the role rules, ownership transfer and the cascade.
/// </summary>
public class OrganizationServiceTests : OrganizationsTestHost
{
    // ---- personal organization and creation ---------------------------------------------------------

    [Fact]
    public async Task The_personal_organization_is_created_once_with_its_user_as_Owner()
    {
        var ann = await SeedUserAsync("ann");

        var first = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var again = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Organizations().EnsurePersonalOrganizationAsync(ann.Id!)));

        first.Id.Should().Be(Organization.PersonalIdFor(ann.Id!));
        first.IsPersonal.Should().BeTrue();
        again.Id.Should().Be(first.Id);
        racing.Select(o => o.Id).Distinct().Should().Equal(first.Id);
        var members = await MembersAsync(first.Id!);
        members.Should().ContainSingle().Which.Role.Should().Be(OrganizationRole.Owner);
        (await Organizations().GetRoleAsync(first.Id!, ann.Id!)).Should().Be(OrganizationRole.Owner);
    }

    [Fact]
    public async Task Creating_an_organization_makes_its_creator_the_Owner()
    {
        var ann = await SeedUserAsync("ann");

        var organization = await Organizations().CreateAsync("  Acme  ", ann.Id!);

        organization.Name.Should().Be("Acme");
        organization.IsPersonal.Should().BeFalse();
        (await MembersAsync(organization.Id!)).Should().ContainSingle()
            .Which.Should().Match(m => ((OrganizationMembership)m!).UserId == ann.Id && ((OrganizationMembership)m!).Role == OrganizationRole.Owner && ((OrganizationMembership)m!).UserName == "ann");
    }

    // ---- invitations ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_invitation_is_mailed_with_a_link_whose_token_is_stored_hashed_and_accepting_it_makes_a_member()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var acme = await SeedOrganizationAsync("Acme", ann);

        await Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Member, ann.Id!);

        var (invitationId, token, mail) = LastLink("bob@test.local");
        mail.Template.Should().Be(OrganizationMailTemplates.Invitation);
        mail.Sensitive.Should().BeTrue("the link is a bearer token");
        var data = JsonSerializer.SerializeToNode(mail.Data)!;
        data["link"]!.GetValue<string>().Should().StartWith(PublicBaseUrl + "/organizations/accept/");
        data["organization"]!.GetValue<string>().Should().Be("Acme");
        data["inviter"]!.GetValue<string>().Should().Be("ann");
        data["valid_for_days"]!.GetValue<int>().Should().Be(14);

        var stored = await LoadAsync<OrganizationInvitation>(invitationId);
        stored!.TokenHash.Should().Be(SparkOrganizations.Hash(token)).And.NotBe(token, "only the SHA-256 is stored");
        stored.ExpiresAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddDays(14), TimeSpan.FromMinutes(1), "the model's expiresAfter (X1)");
        mail.ExpiresAtUtc!.Value.Should().BeBefore(stored.ExpiresAtUtc!.Value);

        var outcome = await Organizations().AcceptAsync(invitationId, token, bob.Id!);

        outcome.Accepted.Should().BeTrue();
        outcome.OrganizationName.Should().Be("Acme");
        (await Organizations().GetRoleAsync(acme.Id!, bob.Id!)).Should().Be(OrganizationRole.Member);
        (await LoadAsync<OrganizationInvitation>(invitationId)).Should().BeNull("a link is spent by accepting it");
    }

    [Fact]
    public async Task A_used_forged_expired_or_misaddressed_link_is_refused_in_the_same_words()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var eve = await SeedUserAsync("eve");
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Viewer, ann.Id!);
        var (invitationId, token, _) = LastLink("bob@test.local");

        var forged = await Organizations().AcceptAsync(invitationId, token + "x", bob.Id!);
        var wrongId = await Organizations().AcceptAsync("OrganizationInvitations/nope", token, bob.Id!);
        var otherCollection = await Organizations().AcceptAsync(acme.Id!, token, bob.Id!);
        (await Organizations().AcceptAsync(invitationId, token, bob.Id!)).Accepted.Should().BeTrue();
        var used = await Organizations().AcceptAsync(invitationId, token, eve.Id!);

        await Organizations().InviteAsync(acme.Id!, "eve@test.local", OrganizationRole.Viewer, ann.Id!);
        var (expiringId, expiringToken, _) = LastLink("eve@test.local");
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<OrganizationInvitation>(expiringId)).ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await session.SaveChangesAsync();
        }
        var expired = await Organizations().AcceptAsync(expiringId, expiringToken, eve.Id!);

        foreach (var refusal in new[] { forged, wrongId, otherCollection, used, expired })
            refusal.Should().Be(OrganizationAcceptance.Invalid);
        (await Organizations().GetRoleAsync(acme.Id!, eve.Id!)).HasValue.Should().BeFalse();
        (await LoadAsync<OrganizationInvitation>(expiringId)).Should().NotBeNull("an expired invitation lingers until the sweep; the check refused it");
    }

    [Fact]
    public async Task A_second_invitation_to_the_same_address_replaces_the_first()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var acme = await SeedOrganizationAsync("Acme", ann);

        await Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Viewer, ann.Id!);
        var (firstId, firstToken, _) = LastLink("bob@test.local");
        await Organizations().InviteAsync(acme.Id!, "BOB@test.local", OrganizationRole.Admin, ann.Id!);

        (await Organizations().AcceptAsync(firstId, firstToken, bob.Id!)).Accepted.Should().BeFalse("the first link was replaced");
        (await InvitationsAsync(acme.Id!)).Should().ContainSingle().Which.Role.Should().Be(OrganizationRole.Admin);
    }

    [Fact]
    public async Task Only_Admins_and_Owners_invite_and_never_as_Owner()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var max = await SeedUserAsync("max");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer), (max, OrganizationRole.Member), (ada, OrganizationRole.Admin));

        var byViewer = () => Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, vic.Id!);
        var byMember = () => Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, max.Id!);
        var asOwner = () => Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Owner, ann.Id!);
        var badAddress = () => Organizations().InviteAsync(acme.Id!, "not an address", OrganizationRole.Viewer, ann.Id!);

        await byViewer.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();
        await byMember.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();
        await asOwner.Should().ThrowAsync<SparkValidationException>();
        await badAddress.Should().ThrowAsync<SparkValidationException>();
        Mailer.Sent.Should().BeEmpty();

        await Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Admin, ada.Id!);
        Mailer.Sent.Should().ContainSingle();
    }

    // ---- join requests: approval requests of kind organizations.join (D26) ---------------------------

    [Fact]
    public async Task A_join_request_is_an_approval_request_every_Owner_and_Admin_is_told_of()
    {
        var ann = await SeedUserAsync("ann");
        var ole = await SeedUserAsync("ole");
        var ada = await SeedUserAsync("ada");
        var max = await SeedUserAsync("max");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann, (ole, OrganizationRole.Owner), (ada, OrganizationRole.Admin), (max, OrganizationRole.Member));

        (await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!)).Should().BeTrue();

        var request = (await PendingJoinRequestsAsync(acme.Id!)).Should().ContainSingle().Which;
        request.RequestedBy.Should().Be(rex.Id);
        request.Subject.Should().Be(new ApprovalSubject("Organization", acme.Id!, rex.Id));
        request.Title.Should().Be("rex asks to join Acme");
        request.Summary.Should().Be("rex (rex@test.local) asks to join Acme as Member.");
        // Owners and Admins decide; before D26 only the Owners were mailed.
        (await LoadAsync<ApprovalRequest>(request.Id))!.DeciderIds.Should().BeEquivalentTo([ann.Id!, ole.Id!, ada.Id!]);
        Mailer.Sent.Where(m => m.Template == SubmittedMail).Select(m => m.To).OrderBy(t => t)
            .Should().Equal("ada@test.local", "ann@test.local", "ole@test.local");
        var link = JsonSerializer.SerializeToNode(Mailer.Sent.Last(m => m.To == "ada@test.local").Data)!["link"]!.GetValue<string>();
        link.Should().Be(PublicBaseUrl + "/po/approval-request/" + Uri.EscapeDataString(request.Id), "the deciders open the request in-app");
        (await InvitationsAsync(acme.Id!)).Should().BeEmpty("a join request is no invitation any more");
    }

    [Fact]
    public async Task An_Owner_approves_a_join_request_in_app_and_the_requester_is_told()
    {
        var ann = await SeedUserAsync("ann");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;

        var (status, _) = await DecideAsync(requestId, "approve", null, ann.Id!);

        status.Should().Be(HttpStatusCode.OK);
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).Should().Be(OrganizationRole.Member);
        var stored = (await LoadAsync<ApprovalRequest>(requestId))!;
        stored.Status.Should().Be(ApprovalStatuses.Approved);
        stored.DecidedBy.Should().Be(ann.Id);
        Mailer.Sent.Should().Contain(m => m.Template == DecidedMail && m.To == "rex@test.local", "the requester is told the decision");
        (await DecideAsync(requestId, "approve", null, ann.Id!)).Status.Should().Be(HttpStatusCode.Conflict, "a request is decided once");
    }

    [Fact]
    public async Task An_Admin_approves_a_join_request_in_app_and_stays_Admin()
    {
        var ann = await SeedUserAsync("ann");
        var ada = await SeedUserAsync("ada");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann, (ada, OrganizationRole.Admin));
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!, OrganizationRole.Viewer);
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;

        (await DecideAsync(requestId, "approve", null, ada.Id!)).Status.Should().Be(HttpStatusCode.OK);

        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).Should().Be(OrganizationRole.Viewer, "the role asked for");
        (await Organizations().GetRoleAsync(acme.Id!, ada.Id!)).Should().Be(OrganizationRole.Admin, "the approver does not change");
    }

    [Fact]
    public async Task A_join_request_is_declined_with_a_reason_and_the_requester_is_told()
    {
        var ann = await SeedUserAsync("ann");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;

        (await DecideAsync(requestId, "reject", "  ", ann.Id!)).Status.Should().Be(HttpStatusCode.BadRequest, "a decline needs a reason");
        (await DecideAsync(requestId, "reject", "We only take staff.", ann.Id!)).Status.Should().Be(HttpStatusCode.OK);

        var stored = (await LoadAsync<ApprovalRequest>(requestId))!;
        stored.Status.Should().Be(ApprovalStatuses.Rejected);
        stored.Reason.Should().Be("We only take staff.");
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).HasValue.Should().BeFalse();
        var mail = Mailer.Sent.Last(m => m.Template == DecidedMail && m.To == "rex@test.local");
        JsonSerializer.SerializeToNode(mail.Data)!["reason"]!.GetValue<string>().Should().Be("We only take staff.");
    }

    [Fact]
    public async Task A_Member_cannot_decide_a_join_request_nor_can_the_requester_approve_their_own()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var rex = await SeedUserAsync("rex");
        var eve = await SeedUserAsync("eve");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;

        // One refusal for all: a Member, the requester, an outsider (the uniform 404).
        (await DecideAsync(requestId, "approve", null, max.Id!)).Status.Should().Be(HttpStatusCode.NotFound);
        (await DecideAsync(requestId, "approve", null, rex.Id!)).Status.Should().Be(HttpStatusCode.NotFound, "no self-approval");
        (await DecideAsync(requestId, "approve", null, eve.Id!)).Status.Should().Be(HttpStatusCode.NotFound);

        (await LoadAsync<ApprovalRequest>(requestId))!.Status.Should().Be(ApprovalStatuses.Pending);
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_decider_demoted_after_the_request_was_raised_cannot_decide_it()
    {
        var ann = await SeedUserAsync("ann");
        var ada = await SeedUserAsync("ada");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann, (ada, OrganizationRole.Admin));
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(ada.Id!, acme.Id!))).Role = OrganizationRole.Member;
            await session.SaveChangesAsync();
        }

        (await DecideAsync(requestId, "approve", null, ada.Id!)).Status.Should().Be(HttpStatusCode.NotFound, "the gate reads the role at decision time");
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_join_request_replaces_the_requesters_pending_one_and_nobody_elses()
    {
        var ann = await SeedUserAsync("ann");
        var rex = await SeedUserAsync("rex");
        var sam = await SeedUserAsync("sam");
        var acme = await SeedOrganizationAsync("Acme", ann);

        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        var first = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;
        await Organizations().RequestToJoinAsync(acme.Id!, sam.Id!);
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!, OrganizationRole.Admin);

        var pending = await PendingJoinRequestsAsync(acme.Id!);
        pending.Select(r => r.RequestedBy).OrderBy(r => r).Should().Equal(rex.Id, sam.Id);
        pending.Single(r => r.RequestedBy == rex.Id).Data["role"].Should().Be("Admin");
        (await LoadAsync<ApprovalRequest>(first))!.Status.Should().Be(ApprovalStatuses.Withdrawn);
    }

    [Fact]
    public async Task A_member_raises_no_join_request()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));

        (await Organizations().RequestToJoinAsync(acme.Id!, max.Id!)).Should().BeFalse();
        Mailer.Sent.Should().BeEmpty();
        (await PendingJoinRequestsAsync(acme.Id!)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_join_request_needs_the_requesters_address_confirmed_but_not_the_deciders()
    {
        // D43 (owner correction 2026-10-10): the deciders see the requester's address with the request, so it
        // must be confirmed, as an inviter's. The decider mails nobody.
        var ann = await SeedUserAsync("ann");
        var ada = await SeedUserAsync("ada", emailConfirmed: false);
        var rex = await SeedUserAsync("rex", emailConfirmed: false);
        var acme = await SeedOrganizationAsync("Acme", ann, (ada, OrganizationRole.Admin));

        var unconfirmed = () => Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        await unconfirmed.Should().ThrowAsync<SparkValidationException>().WithMessage(SparkOrganizations.RequesterUnconfirmedProblem);
        Mailer.Sent.Should().BeEmpty();
        (await PendingJoinRequestsAsync(acme.Id!)).Should().BeEmpty();

        await ConfirmEmailAsync(rex.Id!);
        (await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!)).Should().BeTrue();
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;

        (await DecideAsync(requestId, "approve", null, ada.Id!)).Status.Should().Be(HttpStatusCode.OK);
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).Should().Be(OrganizationRole.Member);
    }

    [Fact]
    public async Task Deleting_an_organization_closes_its_pending_join_requests()
    {
        var ann = await SeedUserAsync("ann");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!);
        var requestId = (await PendingJoinRequestsAsync(acme.Id!)).Single().Id;

        await Organizations().DeleteAsync(acme.Id!, ann.Id!);

        (await LoadAsync<ApprovalRequest>(requestId))!.Status.Should().Be(ApprovalStatuses.Withdrawn, "nobody can decide it any more");
        (await PendingJoinRequestsAsync(acme.Id!)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_join_request_link_mailed_before_the_migration_opens_the_approval_request_it_became()
    {
        // S22/§8: the Owners hold mailed links of join requests raised before D26. The migration keeps the token's
        // hash; the accept endpoint answers a matching link with the request's page, and a forged one learns nothing.
        var ann = await SeedUserAsync("ann");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann);
        const string invitationId = "OrganizationInvitations/0a5e00000000400080000000000000aa";
        await OrganizationJoinRequestMigrationTests.PutLegacyJoinRequestAsync(Store, invitationId, acme.Id!, "rex", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(13));
        await OrganizationJoinRequestUpgrade.RunAsync(Store, DateTime.UtcNow, TimeSpan.FromDays(365));
        var requestId = OrganizationJoinRequestUpgrade.RequestIdFor(invitationId);

        var (status, body) = await SendAsync("/spark/organizations/accept", new { invitationId, token = OrganizationJoinRequestMigrationTests.LegacyToken }, ann.Id);
        var (_, forged) = await SendAsync("/spark/organizations/accept", new { invitationId, token = OrganizationJoinRequestMigrationTests.LegacyToken + "x" }, ann.Id);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("accepted").GetBoolean().Should().BeFalse("nothing is accepted by the link any more");
        body.GetProperty("approvalRequestUrl").GetString().Should().Be("/po/approval-request/" + Uri.EscapeDataString(requestId));
        ApprovalRequestUrlOf(forged).Should().BeNull();
        forged.GetProperty("reason").GetString().Should().Be(OrganizationAcceptance.InvalidReason, "the one refusal");
        (await Organizations().AcceptAsync(invitationId, OrganizationJoinRequestMigrationTests.LegacyToken + "x", ann.Id!)).Should().Be(OrganizationAcceptance.Invalid);

        (await DecideAsync(requestId, "approve", null, ann.Id!)).Status.Should().Be(HttpStatusCode.OK, "the Owner decides it on the request page");
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).Should().Be(OrganizationRole.Member);
    }

    private const string SubmittedMail = "SparkApprovals/Submitted";
    private const string DecidedMail = "SparkApprovals/Decided";

    /// <summary>The pending join requests about <paramref name="organizationId"/>, as server code reads them.</summary>
    private async Task<IReadOnlyList<ApprovalRequestInfo>> PendingJoinRequestsAsync(string organizationId)
    {
        using var scope = Factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISparkApprovals>()
            .GetPendingAsync<OrganizationJoinApproval>(new ApprovalSubject("Organization", organizationId));
    }

    /// <summary>Approves or rejects in-app, as the request page's buttons do (<c>POST /spark/approvals/decide</c>).</summary>
    private Task<(HttpStatusCode Status, JsonElement Body)> DecideAsync(string requestId, string decision, string? reason, string userId)
        => SendAsync("/spark/approvals/decide", new { id = requestId, decision, reason }, userId);

    private static string? ApprovalRequestUrlOf(JsonElement body)
        => body.TryGetProperty("approvalRequestUrl", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;

    // ---- invitations bind to their address (D43) -----------------------------------------------------

    [Fact]
    public async Task An_invitation_opened_by_another_account_is_refused_with_its_reason_and_stays_live()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var eve = await SeedUserAsync("eve");
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().InviteAsync(acme.Id!, "Bob@Test.local ", OrganizationRole.Member, ann.Id!);
        var (invitationId, token, _) = LastLink("Bob@Test.local");

        var byEve = await Organizations().AcceptAsync(invitationId, token, eve.Id!);

        byEve.Should().Be(OrganizationAcceptance.OtherAddress);
        byEve.Reason.Should().Be(OrganizationAcceptance.OtherAddressReason);
        (await Organizations().GetRoleAsync(acme.Id!, eve.Id!)).HasValue.Should().BeFalse();
        (await LoadAsync<OrganizationInvitation>(invitationId)).Should().NotBeNull("a refusal does not spend the link");

        (await Organizations().AcceptAsync(invitationId, token, bob.Id!)).Accepted.Should().BeTrue("the address matches the way Identity normalizes it");
        (await Organizations().GetRoleAsync(acme.Id!, bob.Id!)).Should().Be(OrganizationRole.Member);
    }

    [Fact]
    public async Task An_invitee_whose_matching_address_is_unconfirmed_accepts()
    {
        // D43 (owner correction 2026-10-10): the invitee may not even have had an account when invited,
        // so the address only has to match; the inviter's is the one that must be confirmed.
        var ann = await SeedUserAsync("ann");
        var uma = await SeedUserAsync("uma", emailConfirmed: false);
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().InviteAsync(acme.Id!, "uma@test.local", OrganizationRole.Viewer, ann.Id!);
        var (invitationId, token, _) = LastLink("uma@test.local");

        (await Organizations().AcceptAsync(invitationId, token, uma.Id!)).Accepted.Should().BeTrue();
        (await Organizations().GetRoleAsync(acme.Id!, uma.Id!)).Should().Be(OrganizationRole.Viewer);
    }

    [Fact]
    public async Task An_inviter_without_a_confirmed_email_is_refused_and_nothing_is_stored_or_sent()
    {
        var ann = await SeedUserAsync("ann", emailConfirmed: false);
        var acme = await SeedOrganizationAsync("Acme", ann);

        var invite = () => Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Member, ann.Id!);

        await invite.Should().ThrowAsync<SparkValidationException>().WithMessage(SparkOrganizations.InviterUnconfirmedProblem);
        Mailer.Sent.Should().BeEmpty();
        (await InvitationsAsync(acme.Id!)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_inviter_with_a_confirmed_email_invites()
    {
        var ann = await SeedUserAsync("ann", emailConfirmed: false);
        var acme = await SeedOrganizationAsync("Acme", ann);
        await ConfirmEmailAsync(ann.Id!);

        await Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Member, ann.Id!);

        Mailer.Sent.Select(m => m.To).Should().Equal("bob@test.local");
        (await InvitationsAsync(acme.Id!)).Should().ContainSingle(i => i.Email == "bob@test.local");
    }

    [Fact]
    public async Task The_invite_action_tells_an_inviter_without_a_confirmed_email_why()
    {
        var ann = await SeedUserAsync("ann", emailConfirmed: false);
        var acme = await SeedOrganizationAsync("Acme", ann);

        var (status, body) = await SendAsync("/spark/actions/execute", Wire.Action(OrganizationTypeId, "InviteOrganizationMember", new
        {
            parent = new { id = acme.Id, name = "Organization", objectTypeId = OrganizationTypeId, attributes = Array.Empty<object>() },
        }), ann.Id);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var operations = body.GetProperty("operations").EnumerateArray().ToList();
        operations.Should().ContainSingle(o => o.GetProperty("type").GetString() == "notify"
            && o.GetProperty("message").GetString() == SparkOrganizations.InviterUnconfirmedProblem, "the refusal says why, before any prompt");
        Mailer.Sent.Should().BeEmpty();
        (await InvitationsAsync(acme.Id!)).Should().BeEmpty();
    }

    private async Task ConfirmEmailAsync(string userId)
    {
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<MintPlayer.Spark.Authorization.Identity.SparkUser>(userId)).EmailConfirmed = true;
        await session.SaveChangesAsync();
    }

    // ---- roles: leaving, transferring, deleting ------------------------------------------------------

    [Fact]
    public async Task The_last_Owner_cannot_leave_until_ownership_is_transferred()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));

        var leave = () => Organizations().LeaveAsync(acme.Id!, ann.Id!);
        await leave.Should().ThrowAsync<SparkValidationException>().WithMessage("*Owner*");

        await Organizations().TransferOwnershipAsync(acme.Id!, ann.Id!, max.Id!);

        (await Organizations().GetRoleAsync(acme.Id!, max.Id!)).Should().Be(OrganizationRole.Owner);
        (await Organizations().GetRoleAsync(acme.Id!, ann.Id!)).Should().Be(OrganizationRole.Admin);
        await Organizations().LeaveAsync(acme.Id!, ann.Id!);
        (await Organizations().GetRoleAsync(acme.Id!, ann.Id!)).HasValue.Should().BeFalse("leaving removes access only");
        (await LoadAsync<Organization>(acme.Id!)).Should().NotBeNull();
    }

    [Fact]
    public async Task Only_an_Owner_transfers_ownership_and_only_to_a_member()
    {
        var ann = await SeedUserAsync("ann");
        var ada = await SeedUserAsync("ada");
        var max = await SeedUserAsync("max");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann, (ada, OrganizationRole.Admin), (max, OrganizationRole.Member));

        var byAdmin = () => Organizations().TransferOwnershipAsync(acme.Id!, ada.Id!, max.Id!);
        var toOutsider = () => Organizations().TransferOwnershipAsync(acme.Id!, ann.Id!, out_.Id!);

        await byAdmin.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();
        await toOutsider.Should().ThrowAsync<SparkValidationException>();
        (await Organizations().GetRoleAsync(acme.Id!, ann.Id!)).Should().Be(OrganizationRole.Owner);
    }

    [Fact]
    public async Task Deleting_an_organization_runs_the_data_handlers_then_removes_memberships_and_invitations()
    {
        var sites = new OgDataHandler("sites");
        DataHandlers.Add(sites);
        var ann = await SeedUserAsync("ann");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (ada, OrganizationRole.Admin));
        await Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, ann.Id!);
        var other = await SeedOrganizationAsync("Other", ann);

        var byAdmin = () => Organizations().DeleteAsync(acme.Id!, ada.Id!);
        await byAdmin.Should().ThrowAsync<SparkRowLevelAccessDeniedException>("deleting is the Owner's (D23)");
        sites.Deleted.Should().BeEmpty();

        await Organizations().DeleteAsync(acme.Id!, ann.Id!);

        sites.Deleted.Should().Equal(acme.Id!);
        (await LoadAsync<Organization>(acme.Id!)).Should().BeNull();
        (await MembersAsync(acme.Id!)).Should().BeEmpty();
        (await InvitationsAsync(acme.Id!)).Should().BeEmpty();
        (await MembersAsync(other.Id!)).Should().ContainSingle("another organization is untouched");
    }

    [Fact]
    public async Task A_failing_data_handler_stops_the_deletion_with_the_organization_intact()
    {
        DataHandlers.Add(new OgDataHandler("sites") { Fail = true });
        var ann = await SeedUserAsync("ann");
        var acme = await SeedOrganizationAsync("Acme", ann);

        var delete = () => Organizations().DeleteAsync(acme.Id!, ann.Id!);

        await delete.Should().ThrowAsync<InvalidOperationException>();
        (await LoadAsync<Organization>(acme.Id!)).Should().NotBeNull();
        (await MembersAsync(acme.Id!)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_personal_organization_can_be_neither_left_by_its_user_nor_deleted_nor_transferred()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        await Organizations().InviteAsync(personal.Id!, "max@test.local", OrganizationRole.Admin, ann.Id!);
        var (invitationId, token, _) = LastLink("max@test.local");
        (await Organizations().AcceptAsync(invitationId, token, max.Id!)).Accepted.Should().BeTrue();

        await ((Func<Task>)(() => Organizations().LeaveAsync(personal.Id!, ann.Id!))).Should().ThrowAsync<SparkValidationException>();
        await ((Func<Task>)(() => Organizations().DeleteAsync(personal.Id!, ann.Id!))).Should().ThrowAsync<SparkValidationException>();
        await ((Func<Task>)(() => Organizations().TransferOwnershipAsync(personal.Id!, ann.Id!, max.Id!))).Should().ThrowAsync<SparkValidationException>();
        (await LoadAsync<Organization>(personal.Id!)).Should().NotBeNull();
        (await Organizations().GetRoleAsync(personal.Id!, ann.Id!)).Should().Be(OrganizationRole.Owner);

        await Organizations().LeaveAsync(personal.Id!, max.Id!);
        (await Organizations().GetRoleAsync(personal.Id!, max.Id!)).HasValue.Should().BeFalse("its members leave like any other (D42)");
    }

    // ---- personal organizations take members (D42) ---------------------------------------------------

    [Fact]
    public async Task A_personal_organization_takes_members_by_invitation_and_by_join_request()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var rex = await SeedUserAsync("rex");
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);

        await Organizations().InviteAsync(personal.Id!, "bob@test.local", OrganizationRole.Member, ann.Id!);
        var (invitationId, invitationToken, _) = LastLink("bob@test.local");
        (await Organizations().AcceptAsync(invitationId, invitationToken, bob.Id!)).Accepted.Should().BeTrue();

        // F3: a second user verifying a site held in someone's personal organization asks to join it, and
        // its owner is told and approves it in-app (D26).
        (await Organizations().RequestToJoinAsync(personal.Id!, rex.Id!)).Should().BeTrue();
        Mailer.Sent.Should().Contain(m => m.Template == SubmittedMail && m.To == "ann@test.local");
        var requestId = (await PendingJoinRequestsAsync(personal.Id!)).Single().Id;
        (await DecideAsync(requestId, "approve", null, ann.Id!)).Status.Should().Be(HttpStatusCode.OK);

        (await Organizations().GetRoleAsync(personal.Id!, bob.Id!)).Should().Be(OrganizationRole.Member);
        (await Organizations().GetRoleAsync(personal.Id!, rex.Id!)).Should().Be(OrganizationRole.Member);
        (await Organizations().GetRoleAsync(personal.Id!, ann.Id!)).Should().Be(OrganizationRole.Owner);
    }
}
