using System.Net;
using SeoTester.Entities;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Sites;

/// <summary>
/// Sites through the generic persistent-object endpoints (F2, D18, D21, D23): organization row security, the origin
/// fixed and normalized at creation, and the server-owned fields (verification, latest run) out of a client's reach.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class SiteTests(SeoTesterHostFixture host)
{
    [Fact]
    public async Task An_Admin_adds_a_site_with_its_origin_normalized_and_a_fresh_verification_token()
    {
        var name = $"n{Guid.NewGuid():N}"[..12];

        var (status, body, raw) = await CreateSiteAsync(host, $"HTTPS://{name.ToUpperInvariant()}.Example.com/landing?utm=x", Acme, Ann,
            ("IsVerified", true), ("VerificationToken", "0123456789abcdef0123456789abcdef"), ("LatestScore", 99));

        status.Should().Be(HttpStatusCode.Created, raw);
        var site = (await LoadDocumentAsync<Site>(host, body.GetProperty("result").GetProperty("id").GetString()!))!;
        site.Origin.Should().Be($"https://{name}.example.com");
        site.Host.Should().Be($"{name}.example.com");
        site.OrganizationId.Should().Be(Acme);
        site.IsVerified.Should().BeFalse("a client cannot post its way to a verified site");
        site.LatestScore.HasValue.Should().BeFalse();
        site.VerificationToken.Should().MatchRegex("^[0-9a-f]{32}$");
        site.VerificationToken.Should().NotBe("0123456789abcdef0123456789abcdef");
        site.CreatedBy.Should().Be(Ann);
    }

    [Fact]
    public async Task Without_an_organization_the_site_goes_to_the_creators_personal_one()
    {
        var origin = $"{Guid.NewGuid():N}"[..12] + ".example.org";
        var (status, body, raw) = await CreateSiteAsync(host, origin, organizationId: null, Max);
        var duplicate = await CreateSiteAsync(host, origin, organizationId: null, Max);

        status.Should().Be(HttpStatusCode.Created, raw);
        (await LoadDocumentAsync<Site>(host, body.GetProperty("result").GetProperty("id").GetString()!))!.OrganizationId.Should().Be("Organizations/personal/SparkUsers/max");
        // The default comes from the organizations package (Q-M11a); the duplicate check runs after it (Finalize stage).
        duplicate.Status.Should().Be(HttpStatusCode.BadRequest, duplicate.Raw);
        duplicate.Raw.Should().Contain("already one of this organization's sites");
    }

    [Fact]
    public async Task Only_an_Admin_adds_a_site_and_nobody_adds_one_to_someone_elses_organization()
    {
        var origin = $"{Guid.NewGuid():N}"[..12] + ".example.com";

        (await CreateSiteAsync(host, origin, Acme, Max)).Status.Should().Be(HttpStatusCode.NotFound, "New needs Admin (D23)");
        (await CreateSiteAsync(host, origin, Acme, Bob)).Status.Should().Be(HttpStatusCode.NotFound, "Bob is not in Acme");
        (await CreateSiteAsync(host, origin, Acme, Ann)).Status.Should().Be(HttpStatusCode.Created);
        var duplicate = await CreateSiteAsync(host, "https://" + origin + "/other", Acme, Ann);
        duplicate.Status.Should().Be(HttpStatusCode.BadRequest, duplicate.Raw);
        duplicate.Raw.Should().Contain("already one of this organization's sites");
        (await CreateSiteAsync(host, "ftp://" + origin, Acme, Ann)).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Member_edits_the_profile_but_not_the_origin_the_organization_or_the_verification()
    {
        var site = await SeedSiteAsync(host, Acme);

        var (status, _, raw) = await UpdateAsync(host, SiteTypeId, "Site", site.Id!, Max,
            ("PwaProfile", true), ("Origin", "https://elsewhere.example.org"), ("OrganizationId", "Organizations/personal/SparkUsers/max"),
            ("IsVerified", true), ("VerifiedAtUtc", "2026-10-01T00:00:00Z"), ("LatestGrade", "A"));

        status.Should().Be(HttpStatusCode.OK, raw);
        var stored = (await LoadDocumentAsync<Site>(host, site.Id!))!;
        stored.PwaProfile.Should().BeTrue();
        stored.Origin.Should().Be(site.Origin);
        stored.OrganizationId.Should().Be(Acme);
        stored.IsVerified.Should().BeFalse();
        stored.VerifiedAtUtc.HasValue.Should().BeFalse();
        stored.LatestGrade.Should().BeNull();
        stored.VerificationToken.Should().Be(site.VerificationToken);
    }

    [Fact]
    public async Task A_Viewer_reads_but_does_not_edit_and_an_outsider_sees_nothing()
    {
        var site = await SeedSiteAsync(host, Acme);

        (await LoadAsync(host, SiteTypeId, site.Id!, Vic)).Status.Should().Be(HttpStatusCode.OK);
        (await UpdateAsync(host, SiteTypeId, "Site", site.Id!, Vic, ("PwaProfile", true))).Status.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(host, SiteTypeId, site.Id!, Bob)).Status.Should().Be(HttpStatusCode.NotFound, "missing and forbidden read the same (#453)");
        (await LoadAsync(host, SiteTypeId, site.Id!, null)).Status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }
}
