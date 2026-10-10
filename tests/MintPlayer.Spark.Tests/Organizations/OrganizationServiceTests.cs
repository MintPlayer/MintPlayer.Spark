using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// X11 (<c>docs/seo_analyzer_PRD.md</c> D18, D21–D23, D26) through <see cref="ISparkOrganizations"/> on a
/// booted host: personal organizations, creation, invitations and join requests with their mailed
/// single-use links, the role rules, ownership transfer and the cascade.
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
    public async Task Only_Admins_and_Owners_invite_never_as_Owner_and_never_into_a_personal_organization()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var max = await SeedUserAsync("max");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer), (max, OrganizationRole.Member), (ada, OrganizationRole.Admin));
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);

        var byViewer = () => Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, vic.Id!);
        var byMember = () => Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, max.Id!);
        var asOwner = () => Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Owner, ann.Id!);
        var intoPersonal = () => Organizations().InviteAsync(personal.Id!, "x@test.local", OrganizationRole.Viewer, ann.Id!);
        var badAddress = () => Organizations().InviteAsync(acme.Id!, "not an address", OrganizationRole.Viewer, ann.Id!);

        await byViewer.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();
        await byMember.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();
        await asOwner.Should().ThrowAsync<SparkValidationException>();
        await intoPersonal.Should().ThrowAsync<SparkValidationException>();
        await badAddress.Should().ThrowAsync<SparkValidationException>();
        Mailer.Sent.Should().BeEmpty();

        await Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Admin, ada.Id!);
        Mailer.Sent.Should().ContainSingle();
    }

    // ---- join requests -------------------------------------------------------------------------------

    [Fact]
    public async Task A_join_request_mails_every_Owner_and_an_Owner_or_Admin_lets_the_requester_in()
    {
        var ann = await SeedUserAsync("ann");
        var ole = await SeedUserAsync("ole");
        var ada = await SeedUserAsync("ada");
        var max = await SeedUserAsync("max");
        var rex = await SeedUserAsync("rex");
        var acme = await SeedOrganizationAsync("Acme", ann, (ole, OrganizationRole.Owner), (ada, OrganizationRole.Admin), (max, OrganizationRole.Member));

        (await Organizations().RequestToJoinAsync(acme.Id!, rex.Id!)).Should().BeTrue();

        Mailer.Sent.Select(m => m.To).OrderBy(t => t).Should().Equal("ann@test.local", "ole@test.local");
        var (requestId, token, mail) = LastLink("ann@test.local");
        mail.Template.Should().Be(OrganizationMailTemplates.JoinRequest);
        var data = JsonSerializer.SerializeToNode(mail.Data)!;
        data["requester"]!.GetValue<string>().Should().Be("rex");
        data["requester_email"]!.GetValue<string>().Should().Be("rex@test.local");

        // A Member holding the link cannot let anyone in, nor can the requester let themselves in.
        (await Organizations().AcceptAsync(requestId, token, max.Id!)).Should().Be(OrganizationAcceptance.Invalid);
        (await Organizations().AcceptAsync(requestId, token, rex.Id!)).Should().Be(OrganizationAcceptance.Invalid);

        var outcome = await Organizations().AcceptAsync(requestId, token, ada.Id!);

        outcome.Accepted.Should().BeTrue();
        (await Organizations().GetRoleAsync(acme.Id!, rex.Id!)).Should().Be(OrganizationRole.Member);
        (await Organizations().GetRoleAsync(acme.Id!, ada.Id!)).Should().Be(OrganizationRole.Admin, "the approver does not change");
        (await Organizations().AcceptAsync(requestId, token, ann.Id!)).Should().Be(OrganizationAcceptance.Invalid, "the link is spent");
    }

    [Fact]
    public async Task A_member_or_a_personal_organization_raises_no_join_request()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);

        (await Organizations().RequestToJoinAsync(acme.Id!, max.Id!)).Should().BeFalse();
        (await Organizations().RequestToJoinAsync(personal.Id!, max.Id!)).Should().BeFalse();
        Mailer.Sent.Should().BeEmpty();
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
    public async Task A_personal_organization_can_be_neither_left_nor_deleted_nor_transferred()
    {
        var ann = await SeedUserAsync("ann");
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);

        await ((Func<Task>)(() => Organizations().LeaveAsync(personal.Id!, ann.Id!))).Should().ThrowAsync<SparkValidationException>();
        await ((Func<Task>)(() => Organizations().DeleteAsync(personal.Id!, ann.Id!))).Should().ThrowAsync<SparkValidationException>();
        (await LoadAsync<Organization>(personal.Id!)).Should().NotBeNull();
    }
}
