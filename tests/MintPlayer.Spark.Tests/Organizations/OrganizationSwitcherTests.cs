using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// The organization switcher (<c>docs/seo_analyzer_PRD.md</c> Q-M11a): the choice is a cookie checked against the
/// caller's memberships on every request; lists of organization-owned rows follow it, a detail page, a sub-query
/// and the organization list do not, a new row without an organization lands in it, and a choice the caller is
/// no longer a member of counts as "All organizations".
/// </summary>
public class OrganizationSwitcherTests : OrganizationsTestHost
{
    private const string Cookie = "spark-organization";

    private static string Chosen(string organizationId) => Cookie + "=" + Uri.EscapeDataString(organizationId);

    private static object NewNote(string title) => Wire.Typed(NoteTypeId, new
    {
        persistentObject = new
        {
            name = "OgNote",
            objectTypeId = NoteTypeId.ToString(),
            attributes = new object[] { new { name = "Title", value = title, isValueChanged = true } },
        },
    });

    private async Task<(HttpStatusCode Status, JsonElement Body, string? SetCookie)> GetCurrentAsync(string userId, string? cookie = null)
    {
        using var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/spark/organizations/current");
        request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith(Cookie + "=")) : null;
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone(), setCookie);
    }

    [Fact]
    public async Task Lists_follow_the_chosen_organization_and_details_sub_queries_and_the_organization_list_do_not()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var globex = await SeedOrganizationAsync("Globex", ann);
        var other = await SeedOrganizationAsync("Other", bob);
        var plan = await SeedNoteAsync("plan", acme.Id);
        var memo = await SeedNoteAsync("memo", globex.Id);

        var all = await QueryIdsAsync(Wire.Query(NotesQueryId), ann.Id!);
        var acmeOnly = await QueryIdsAsync(Wire.Query(NotesQueryId), ann.Id!, Chosen(acme.Id!));
        var detail = await SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, id: memo.Id), ann.Id, Chosen(acme.Id!));
        var members = await QueryIdsAsync(Wire.Query(MembersQueryId, new { parentId = globex.Id, parentType = "Organization" }), ann.Id!, Chosen(acme.Id!));
        var organizations = await QueryIdsAsync(Wire.Query(OrganizationsQueryId), ann.Id!, Chosen(acme.Id!));
        var foreignChoice = await QueryIdsAsync(Wire.Query(NotesQueryId), ann.Id!, Chosen(other.Id!));

        all.Should().BeEquivalentTo([plan.Id!, memo.Id!]);
        acmeOnly.Should().Equal(plan.Id!);
        detail.Status.Should().Be(HttpStatusCode.OK, "a row of another organization still opens by its link");
        members.Should().ContainSingle("a sub-query shows its parent's rows whatever the switcher says");
        organizations.Should().BeEquivalentTo([acme.Id!, globex.Id!]);   // the switcher picks from the full organization list
        foreignChoice.Should().BeEquivalentTo([plan.Id!, memo.Id!]);   // a cookie naming someone else's organization is ignored, never widened
    }

    [Fact]
    public async Task A_new_row_without_an_organization_lands_in_the_chosen_one_else_in_the_personal_one()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var acme = await SeedOrganizationAsync("Acme", ann);
        var viewOnly = await SeedOrganizationAsync("ViewOnly", vic, (ann, OrganizationRole.Viewer));

        var (chosenStatus, chosenBody) = await SendAsync("/spark/po/create", NewNote("chosen"), ann.Id, Chosen(acme.Id!));
        var (noneStatus, noneBody) = await SendAsync("/spark/po/create", NewNote("none"), ann.Id);
        var (viewerStatus, viewerBody) = await SendAsync("/spark/po/create", NewNote("viewer"), ann.Id, Chosen(viewOnly.Id!));

        chosenStatus.Should().Be(HttpStatusCode.Created);
        noneStatus.Should().Be(HttpStatusCode.Created);
        viewerStatus.Should().Be(HttpStatusCode.Created);
        (await LoadAsync<OgNote>(chosenBody.GetProperty("result").GetProperty("id").GetString()!))!.OrganizationId.Should().Be(acme.Id);
        (await LoadAsync<OgNote>(noneBody.GetProperty("result").GetProperty("id").GetString()!))!.OrganizationId.Should().Be(Organization.PersonalIdFor(ann.Id!));
        (await LoadAsync<OgNote>(viewerBody.GetProperty("result").GetProperty("id").GetString()!))!.OrganizationId
            .Should().Be(Organization.PersonalIdFor(ann.Id!), "a Viewer cannot create there, so the personal organization is the default");
    }

    [Fact]
    public async Task The_switcher_endpoint_offers_the_callers_organizations_and_remembers_only_a_membership()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var acme = await SeedOrganizationAsync("Acme", ann);
        var other = await SeedOrganizationAsync("Other", bob);

        var (status, body, _) = await GetCurrentAsync(ann.Id!);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null, "no choice is All organizations");
        body.GetProperty("organizations").EnumerateArray().Select(o => o.GetProperty("id").GetString())
            .Should().Equal(Organization.PersonalIdFor(ann.Id!), acme.Id);

        var (pick, picked) = await SendAsync("/spark/organizations/current", new { organizationId = acme.Id }, ann.Id);
        pick.Should().Be(HttpStatusCode.OK);
        picked.GetProperty("current").GetString().Should().Be(acme.Id);

        (await SendAsync("/spark/organizations/current", new { organizationId = other.Id }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "an organization the caller is not in reads as missing (#453)");
        (await SendAsync("/spark/organizations/current", new { organizationId = (string?)null }, ann.Id)).Body
            .GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);

        var (_, remembered, _) = await GetCurrentAsync(ann.Id!, Chosen(acme.Id!));
        remembered.GetProperty("current").GetString().Should().Be(acme.Id);
    }

    [Fact]
    public async Task A_member_who_leaves_the_chosen_organization_is_back_on_all_and_the_cookie_is_dropped()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var acme = await SeedOrganizationAsync("Acme", ann, (bob, OrganizationRole.Member));
        var globex = await SeedOrganizationAsync("Globex", bob);
        var plan = await SeedNoteAsync("plan", acme.Id);
        var memo = await SeedNoteAsync("memo", globex.Id);
        (await QueryIdsAsync(Wire.Query(NotesQueryId), bob.Id!, Chosen(acme.Id!))).Should().Equal(plan.Id!);

        await Organizations().LeaveAsync(acme.Id!, bob.Id!);

        (await QueryIdsAsync(Wire.Query(NotesQueryId), bob.Id!, Chosen(acme.Id!))).Should().Equal(memo.Id!);
        var (_, body, setCookie) = await GetCurrentAsync(bob.Id!, Chosen(acme.Id!));
        body.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
        setCookie.Should().NotBeNull("the stale choice is deleted");
        setCookie!.Should().Contain("expires=");
    }
}
