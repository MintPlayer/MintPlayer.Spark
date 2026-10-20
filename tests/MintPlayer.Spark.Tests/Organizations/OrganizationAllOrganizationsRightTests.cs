using System.Net;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// <c>AllOrganizations/{Type}</c> (warehouse WM3, lead decision 2026-10-20): a group granted it reads every organization's
/// rows of that type; without it, the membership rule as before. Red before: no right lifted the organization filter, so a
/// warehouse's staff saw only the owners they happened to be members of.
/// </summary>
public class OrganizationAllOrganizationsRightTests : OrganizationsTestHost
{
    private async Task<(string StaffId, string OwnNote, string ForeignNote)> SeedAsync()
    {
        var staff = await SeedUserAsync("staff");
        var ann = await SeedUserAsync("ann");
        var own = await SeedOrganizationAsync("Own", staff);
        var other = await SeedOrganizationAsync("Other", ann);
        var ownNote = await SeedNoteAsync("own", own.Id);
        var foreignNote = await SeedNoteAsync("foreign", other.Id);
        return (staff.Id!, ownNote.Id!, foreignNote.Id!);
    }

    private async Task<HttpStatusCode> EditAsync(string id, string userId)
    {
        var payload = Wire.Typed(NoteTypeId, new
        {
            persistentObject = new
            {
                id,
                etag = await StoredEtag.OfAsync(Store, id),
                name = "OgNote",
                objectTypeId = NoteTypeId.ToString(),
                attributes = new[] { new { name = "Title", value = "edited", isValueChanged = true } },
            },
        }, id);
        return (await SendAsync("/spark/po/update", payload, userId)).Status;
    }

    [Fact]
    public async Task Without_the_grant_a_caller_sees_only_the_organizations_they_belong_to()
    {
        var (staff, own, foreign) = await SeedAsync();

        (await QueryIdsAsync(Wire.Query(NotesQueryId), staff)).Should().Equal(own);
        (await SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, id: foreign), staff)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_the_grant_a_caller_reads_every_organizations_rows_of_that_type_but_writes_only_their_own()
    {
        ExtraRights.Add(new Right { Key = "staff-all", Resource = $"{OrganizationRights.AllOrganizations}/OgNote", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        var (staff, own, foreign) = await SeedAsync();

        (await QueryIdsAsync(Wire.Query(NotesQueryId), staff)).OrderBy(i => i, StringComparer.Ordinal).ToList()
            .Should().Equal(new[] { own, foreign }.OrderBy(i => i, StringComparer.Ordinal).ToArray());
        (await SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, id: foreign), staff)).Status.Should().Be(HttpStatusCode.OK);
        (await EditAsync(foreign, staff)).Should().NotBe(HttpStatusCode.OK, "the grant covers Query and Read only");
    }
}
