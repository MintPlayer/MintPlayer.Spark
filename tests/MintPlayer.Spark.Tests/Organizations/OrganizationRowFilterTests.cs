using System.Net;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// The row policy (<c>docs/seo_analyzer_PRD.md</c> X11, §4.2) through the real route table: an
/// <see cref="IOrganizationOwned"/> row is reached only by members of its organization with the role the
/// action requires, a non-member gets the missing-row 404 and never sees it listed (#453), and the policy
/// ANDs with the expiration policy (X1). Also the organization pages of the generic UI: create, the
/// members and invitations sub-queries, role edits, delete and the accept endpoint.
/// </summary>
public class OrganizationRowFilterTests : OrganizationsTestHost
{
    private static object Load(Guid typeId, string id) => Wire.Typed(typeId, id: id);
    private static object Load(string typeId, string id) => Wire.Typed(typeId, id: id);

    private async Task<object> EditNoteAsync(string id, string title) => Wire.Typed(NoteTypeId, new
    {
        persistentObject = new
        {
            id,
            etag = await StoredEtag.OfAsync(Store, id),
            name = "OgNote",
            objectTypeId = NoteTypeId.ToString(),
            attributes = new[] { new { name = "Title", value = title, isValueChanged = true } },
        },
    }, id);

    private async Task<object> EditRoleAsync(string membershipId, OrganizationRole role) => Wire.Typed(MembershipTypeId, new
    {
        persistentObject = new
        {
            id = membershipId,
            etag = await StoredEtag.OfAsync(Store, membershipId),
            name = "OrganizationMembership",
            objectTypeId = MembershipTypeId,
            attributes = new[] { new { name = "Role", value = role.ToString(), isValueChanged = true } },
        },
    }, membershipId);

    private static object NewNote(string title, string organizationId) => Wire.Typed(NoteTypeId, new
    {
        persistentObject = new
        {
            name = "OgNote",
            objectTypeId = NoteTypeId.ToString(),
            attributes = new object[]
            {
                new { name = "Title", value = title, isValueChanged = true },
                new { name = "OrganizationId", value = organizationId, isValueChanged = true },
            },
        },
    });

    // ---- organization-owned rows ----------------------------------------------------------------------

    [Fact]
    public async Task A_member_reads_and_lists_the_organizations_rows_and_a_non_member_gets_404_and_nothing()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer));
        var other = await SeedOrganizationAsync("Other", out_);
        var note = await SeedNoteAsync("plan", acme.Id);
        var foreign = await SeedNoteAsync("foreign", other.Id);
        await SeedNoteAsync("orphan", null);

        var (viewerLoad, _) = await SendAsync("/spark/po/load", Load(NoteTypeId, note.Id!), vic.Id);
        var (outsiderLoad, _) = await SendAsync("/spark/po/load", Load(NoteTypeId, note.Id!), out_.Id);
        var (anonymousLoad, _) = await SendAsync("/spark/po/load", Load(NoteTypeId, note.Id!), null);

        viewerLoad.Should().Be(HttpStatusCode.OK);
        outsiderLoad.Should().Be(HttpStatusCode.NotFound, "missing and forbidden read the same (#453)");
        anonymousLoad.Should().Be(HttpStatusCode.Unauthorized, "an anonymous caller is asked to sign in, before any row is looked at");
        (await QueryIdsAsync(Wire.Query(NotesQueryId), vic.Id!)).Should().Equal(note.Id!);
        (await QueryIdsAsync(Wire.Query(NotesQueryId), out_.Id!)).Should().Equal(foreign.Id!);
    }

    [Fact]
    public async Task The_role_each_action_requires_is_honoured()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var max = await SeedUserAsync("max");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer), (max, OrganizationRole.Member), (ada, OrganizationRole.Admin));
        var note = await SeedNoteAsync("plan", acme.Id);

        var (viewerEdit, _) = await SendAsync("/spark/po/update", await EditNoteAsync(note.Id!, "by viewer"), vic.Id);
        viewerEdit.Should().Be(HttpStatusCode.NotFound, "Edit needs Member");
        var (memberEdit, _) = await SendAsync("/spark/po/update", await EditNoteAsync(note.Id!, "by member"), max.Id);
        memberEdit.Should().Be(HttpStatusCode.OK);
        (await LoadAsync<OgNote>(note.Id!))!.Title.Should().Be("by member");

        var (viewerCreate, _) = await SendAsync("/spark/po/create", NewNote("v", acme.Id!), vic.Id);
        var (memberCreate, _) = await SendAsync("/spark/po/create", NewNote("m", acme.Id!), max.Id);
        viewerCreate.Should().Be(HttpStatusCode.NotFound, "New needs Member (WITH CHECK)");
        memberCreate.Should().Be(HttpStatusCode.Created);

        var (memberDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id, etag: await StoredEtag.OfAsync(Store, note.Id!)), max.Id);
        memberDelete.Should().Be(HttpStatusCode.NotFound, "Delete needs Admin");
        var (adminDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id, etag: await StoredEtag.OfAsync(Store, note.Id!)), ada.Id);
        adminDelete.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        (await LoadAsync<OgNote>(note.Id!)).Should().BeNull();
    }

    [Fact]
    public async Task A_member_cannot_create_a_row_in_or_move_one_to_an_organization_they_do_not_belong_to()
    {
        var ann = await SeedUserAsync("ann");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var other = await SeedOrganizationAsync("Other", out_);
        var note = await SeedNoteAsync("plan", acme.Id);

        var (create, _) = await SendAsync("/spark/po/create", NewNote("smuggled", other.Id!), ann.Id);
        var (move, _) = await SendAsync("/spark/po/update", Wire.Typed(NoteTypeId, new
        {
            persistentObject = new
            {
                id = note.Id,
                etag = await StoredEtag.OfAsync(Store, note.Id!),
                name = "OgNote",
                objectTypeId = NoteTypeId.ToString(),
                attributes = new[] { new { name = "OrganizationId", value = other.Id, isValueChanged = true } },
            },
        }, note.Id), ann.Id);

        create.Should().Be(HttpStatusCode.NotFound);
        move.Should().Be(HttpStatusCode.NotFound, "WITH CHECK judges the resulting row");
        (await LoadAsync<OgNote>(note.Id!))!.OrganizationId.Should().Be(acme.Id);
    }

    [Fact]
    public async Task The_membership_filter_ANDs_with_the_expiration_policy()
    {
        var ann = await SeedUserAsync("ann");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var live = await SeedNoteAsync("live", acme.Id, DateTime.UtcNow.AddDays(1));
        var expired = await SeedNoteAsync("expired", acme.Id, DateTime.UtcNow.AddMinutes(-1));

        var (expiredLoad, _) = await SendAsync("/spark/po/load", Load(NoteTypeId, expired.Id!), ann.Id);

        expiredLoad.Should().Be(HttpStatusCode.NotFound, "even the Owner does not see an expired row");
        (await QueryIdsAsync(Wire.Query(NotesQueryId), ann.Id!)).Should().Equal(live.Id!);
    }

    [Fact]
    public async Task A_membership_counts_on_the_next_request_after_it_is_saved_or_removed()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var note = await SeedNoteAsync("plan", acme.Id);
        (await SendAsync("/spark/po/load", Load(NoteTypeId, note.Id!), bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);

        await Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Viewer, ann.Id!);
        var (invitationId, token, _) = LastLink("bob@test.local");
        (await Organizations().AcceptAsync(invitationId, token, bob.Id!)).Accepted.Should().BeTrue();
        (await SendAsync("/spark/po/load", Load(NoteTypeId, note.Id!), bob.Id)).Status.Should().Be(HttpStatusCode.OK, "read by id prefix, no index to wait for");

        await Organizations().LeaveAsync(acme.Id!, bob.Id!);
        (await SendAsync("/spark/po/load", Load(NoteTypeId, note.Id!), bob.Id)).Status.Should().Be(HttpStatusCode.NotFound, "access ends with the membership");
    }

    [Fact]
    public async Task Without_AddOrganizations_the_packages_types_are_closed_to_everyone()
    {
        // The library's model and rights come with the reference; its row policy only with AddOrganizations().
        WithPackage = false;
        var ann = await SeedUserAsync("ann");
        var organization = new Organization { Id = Organization.NewId(), Name = "Acme", CreatedBy = ann.Id };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(organization, organization.Id);
            await session.StoreAsync(new OrganizationMembership
            {
                Id = OrganizationMembership.IdFor(ann.Id!, organization.Id!),
                OrganizationId = organization.Id,
                UserId = ann.Id,
                Role = OrganizationRole.Owner,
            });
            await session.SaveChangesAsync();
        }

        (await SendAsync("/spark/po/load", Load(OrganizationTypeId, organization.Id!), ann.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await QueryIdsAsync(Wire.Query(OrganizationsQueryId), ann.Id!)).Should().BeEmpty();
        var (membersStatus, _) = await SendAsync("/spark/queries/execute", Wire.Query(MembersQueryId, new { parentId = organization.Id, parentType = "Organization" }), ann.Id);
        membersStatus.Should().Be(HttpStatusCode.NotFound, "the parent organization is unreadable, so its sub-query is refused");
    }

    // ---- the organization pages ---------------------------------------------------------------------

    [Fact]
    public async Task Creating_an_organization_in_the_generic_UI_makes_the_caller_its_Owner()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");

        var (status, body) = await SendAsync("/spark/po/create", Wire.Typed(OrganizationTypeId, new
        {
            persistentObject = new
            {
                name = "Organization",
                objectTypeId = OrganizationTypeId,
                attributes = new object[]
                {
                    new { name = "Name", value = "Acme", isValueChanged = true },
                    new { name = "IsPersonal", value = true, isValueChanged = true },
                },
            },
        }), ann.Id);

        status.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        id.Should().StartWith("Organizations/");
        var stored = await LoadAsync<Organization>(id);
        stored!.IsPersonal.Should().BeFalse("nobody posts IsPersonal");
        stored.CreatedBy.Should().Be(ann.Id);
        (await Organizations().GetRoleAsync(id, ann.Id!)).Should().Be(OrganizationRole.Owner);
        (await QueryIdsAsync(Wire.Query(OrganizationsQueryId), ann.Id!)).Should().Equal(id);
        (await QueryIdsAsync(Wire.Query(OrganizationsQueryId), bob.Id!)).Should().BeEmpty();
        (await SendAsync("/spark/po/load", Load(OrganizationTypeId, id), bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_see_the_members_list_and_only_Admins_see_the_invitations()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer), (ada, OrganizationRole.Admin));
        await Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, ann.Id!);
        var parent = new { parentId = acme.Id, parentType = "Organization" };

        var viewerMembers = await QueryIdsAsync(Wire.Query(MembersQueryId, parent), vic.Id!);
        var viewerInvitations = await QueryIdsAsync(Wire.Query(InvitationsQueryId, parent), vic.Id!);
        var adminInvitations = await QueryIdsAsync(Wire.Query(InvitationsQueryId, parent), ada.Id!);

        viewerMembers.Should().HaveCount(3);
        viewerInvitations.Should().BeEmpty("invitations are the Admins' (D23)");
        adminInvitations.Should().ContainSingle();
    }

    [Fact]
    public async Task Role_edits_keep_the_Owner_role_to_Owners_and_never_drop_the_last_Owner()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer), (ada, OrganizationRole.Admin));
        var vicMembership = OrganizationMembership.IdFor(vic.Id!, acme.Id!);
        var annMembership = OrganizationMembership.IdFor(ann.Id!, acme.Id!);

        (await SendAsync("/spark/po/update", await EditRoleAsync(vicMembership, OrganizationRole.Member), vic.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a Viewer edits no membership");
        (await SendAsync("/spark/po/update", await EditRoleAsync(vicMembership, OrganizationRole.Member), ada.Id)).Status
            .Should().Be(HttpStatusCode.OK, "an Admin changes roles");
        (await SendAsync("/spark/po/update", await EditRoleAsync(vicMembership, OrganizationRole.Owner), ada.Id)).Status
            .Should().Be(HttpStatusCode.BadRequest, "only an Owner gives the Owner role");
        (await SendAsync("/spark/po/update", await EditRoleAsync(annMembership, OrganizationRole.Admin), ann.Id)).Status
            .Should().Be(HttpStatusCode.BadRequest, "the last Owner cannot step down");
        (await SendAsync("/spark/po/delete", Wire.Typed(MembershipTypeId, id: annMembership, etag: await StoredEtag.OfAsync(Store, annMembership)), ann.Id)).Status
            .Should().Be(HttpStatusCode.BadRequest, "nor be removed");

        (await LoadAsync<OrganizationMembership>(vicMembership))!.Role.Should().Be(OrganizationRole.Member);
        (await LoadAsync<OrganizationMembership>(annMembership))!.Role.Should().Be(OrganizationRole.Owner);
    }

    [Fact]
    public async Task In_a_personal_organization_members_are_edited_and_removed_but_the_Owner_role_never_moves()
    {
        // D42: a personal organization's other members behave as anywhere; its user stays its only Owner.
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var max = await SeedUserAsync("max");
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        using (var session = Store.OpenAsyncSession())
        {
            foreach (var user in new[] { vic, max })
                await session.StoreAsync(new OrganizationMembership
                {
                    Id = OrganizationMembership.IdFor(user.Id!, personal.Id!),
                    OrganizationId = personal.Id,
                    UserId = user.Id,
                    UserName = user.UserName,
                    Role = OrganizationRole.Viewer,
                    JoinedAtUtc = DateTime.UtcNow,
                });
            await session.SaveChangesAsync();
        }
        var vicMembership = OrganizationMembership.IdFor(vic.Id!, personal.Id!);
        var maxMembership = OrganizationMembership.IdFor(max.Id!, personal.Id!);
        var annMembership = OrganizationMembership.IdFor(ann.Id!, personal.Id!);

        (await SendAsync("/spark/po/update", await EditRoleAsync(vicMembership, OrganizationRole.Member), ann.Id)).Status
            .Should().Be(HttpStatusCode.OK, "the Owner changes a member's role");
        (await SendAsync("/spark/po/update", await EditRoleAsync(vicMembership, OrganizationRole.Owner), ann.Id)).Status
            .Should().Be(HttpStatusCode.BadRequest, "nobody else becomes Owner of a personal organization");
        (await SendAsync("/spark/po/update", await EditRoleAsync(annMembership, OrganizationRole.Admin), ann.Id)).Status
            .Should().Be(HttpStatusCode.BadRequest, "its user stays Owner");
        (await SendAsync("/spark/po/delete", Wire.Typed(MembershipTypeId, id: maxMembership, etag: await StoredEtag.OfAsync(Store, maxMembership)), ann.Id)).Status
            .Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        (await LoadAsync<OrganizationMembership>(vicMembership))!.Role.Should().Be(OrganizationRole.Member);
        (await LoadAsync<OrganizationMembership>(maxMembership)).Should().BeNull();
        (await LoadAsync<OrganizationMembership>(annMembership))!.Role.Should().Be(OrganizationRole.Owner);
    }

    [Fact]
    public async Task Deleting_an_organization_in_the_generic_UI_is_the_Owners_and_cascades()
    {
        var sites = new OgDataHandler("sites");
        DataHandlers.Add(sites);
        var ann = await SeedUserAsync("ann");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (ada, OrganizationRole.Admin));
        await Organizations().InviteAsync(acme.Id!, "x@test.local", OrganizationRole.Viewer, ann.Id!);
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);

        var (byAdmin, _) = await SendAsync("/spark/po/delete", Wire.Typed(OrganizationTypeId, id: acme.Id, etag: await StoredEtag.OfAsync(Store, acme.Id!)), ada.Id);
        var (personalDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(OrganizationTypeId, id: personal.Id, etag: await StoredEtag.OfAsync(Store, personal.Id!)), ann.Id);
        byAdmin.Should().Be(HttpStatusCode.NotFound, "Delete needs Owner");
        personalDelete.Should().Be(HttpStatusCode.BadRequest);
        sites.Deleted.Should().BeEmpty();

        var (byOwner, _) = await SendAsync("/spark/po/delete", Wire.Typed(OrganizationTypeId, id: acme.Id, etag: await StoredEtag.OfAsync(Store, acme.Id!)), ann.Id);

        byOwner.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        sites.Deleted.Should().Equal(acme.Id!);
        (await LoadAsync<Organization>(acme.Id!)).Should().BeNull();
        (await MembersAsync(acme.Id!)).Should().BeEmpty();
        (await InvitationsAsync(acme.Id!)).Should().BeEmpty();
    }

    [Fact]
    public async Task Leaving_through_the_custom_action_removes_the_callers_membership()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));

        var (status, _) = await SendAsync("/spark/actions/execute", Wire.Action(OrganizationTypeId, OrganizationActionNames.Leave, new
        {
            parent = new { id = acme.Id, name = "Organization", objectTypeId = OrganizationTypeId, attributes = Array.Empty<object>() },
        }), max.Id);

        status.Should().Be(HttpStatusCode.OK);
        (await Organizations().GetRoleAsync(acme.Id!, max.Id!)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task Transferring_ownership_through_the_custom_action_swaps_the_roles()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));
        var maxMembership = OrganizationMembership.IdFor(max.Id!, acme.Id!);

        var (byMember, _) = await SendAsync("/spark/actions/execute", Wire.Action(MembershipTypeId, OrganizationActionNames.TransferOwnership, new
        {
            selectedItemIds = new[] { maxMembership },
            queryId = MembersQueryId,
            parentId = acme.Id,
            parentType = "Organization",
        }), max.Id);
        var (byOwner, _) = await SendAsync("/spark/actions/execute", Wire.Action(MembershipTypeId, OrganizationActionNames.TransferOwnership, new
        {
            selectedItemIds = new[] { maxMembership },
            queryId = MembersQueryId,
            parentId = acme.Id,
            parentType = "Organization",
        }), ann.Id);

        byMember.Should().Be(HttpStatusCode.NotFound, "the row gate asks Owner for this action");
        byOwner.Should().Be(HttpStatusCode.OK);
        (await Organizations().GetRoleAsync(acme.Id!, max.Id!)).Should().Be(OrganizationRole.Owner);
        (await Organizations().GetRoleAsync(acme.Id!, ann.Id!)).Should().Be(OrganizationRole.Admin);
    }

    // ---- the accept endpoint ------------------------------------------------------------------------

    [Fact]
    public async Task The_accept_endpoint_redeems_a_link_for_the_signed_in_user_only()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var acme = await SeedOrganizationAsync("Acme", ann);
        await Organizations().InviteAsync(acme.Id!, "bob@test.local", OrganizationRole.Member, ann.Id!);
        var (invitationId, token, _) = LastLink("bob@test.local");

        var eve = await SeedUserAsync("eve");

        var (anonymous, _) = await SendAsync("/spark/organizations/accept", new { invitationId, token }, null);
        var (forged, forgedBody) = await SendAsync("/spark/organizations/accept", new { invitationId, token = token + "x" }, bob.Id);
        var (otherAccount, otherBody) = await SendAsync("/spark/organizations/accept", new { invitationId, token }, eve.Id);
        var (accepted, body) = await SendAsync("/spark/organizations/accept", new { invitationId, token }, bob.Id);

        anonymous.Should().Be(HttpStatusCode.Unauthorized);
        forged.Should().Be(HttpStatusCode.OK);
        forgedBody.GetProperty("accepted").GetBoolean().Should().BeFalse();
        forgedBody.GetProperty("problem").GetString().Should().Be(OrganizationAcceptance.InvalidProblem);
        forgedBody.GetProperty("reason").GetString().Should().Be(OrganizationAcceptance.InvalidReason);
        otherAccount.Should().Be(HttpStatusCode.OK);
        otherBody.GetProperty("accepted").GetBoolean().Should().BeFalse("D43: the account must own the invited address");
        otherBody.GetProperty("problem").GetString().Should().Be(OrganizationAcceptance.OtherAddressProblem);
        otherBody.GetProperty("reason").GetString().Should().Be(OrganizationAcceptance.OtherAddressReason);
        accepted.Should().Be(HttpStatusCode.OK);
        body.GetProperty("accepted").GetBoolean().Should().BeTrue();
        body.GetProperty("organizationId").GetString().Should().Be(acme.Id);
        (await Organizations().GetRoleAsync(acme.Id!, bob.Id!)).Should().Be(OrganizationRole.Member);
    }
}
