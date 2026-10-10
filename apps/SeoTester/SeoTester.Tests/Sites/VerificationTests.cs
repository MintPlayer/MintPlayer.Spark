using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.Settings;
using SeoTester.Sites;
using SeoTester.Tests._Infrastructure;
using SeoTester.Verification;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Sites;

/// <summary>
/// Ownership verification per organization (F3, D18, D29, D42) through the VerifySite action, with DNS and the site
/// answered from tables, and the periodic re-check.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class VerificationTests(SeoTesterHostFixture host)
{
    [Fact]
    public async Task A_TXT_record_at_underscore_seotester_verifies_the_site_for_its_organization()
    {
        var site = await SeedSiteAsync(host, Acme);
        host.Dns.Txt[OwnershipProof.RecordName(site.Host)] = ["unrelated", OwnershipProof.ExpectedValue(site.VerificationToken!)];

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "VerifySite", site.Id!, Ann);

        status.Should().Be(HttpStatusCode.OK, raw);
        body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("Verified");
        var stored = (await LoadDocumentAsync<Site>(host, site.Id!))!;
        stored.IsVerified.Should().BeTrue();
        stored.VerificationMethod.Should().Be("Dns");
        stored.VerifiedAtUtc.HasValue.Should().BeTrue();
        (await LogKindsAsync(site.Id!)).Should().Contain(AuditLogKinds.SiteVerified);
    }

    [Fact]
    public async Task The_well_known_file_and_the_meta_tag_verify_too_but_not_through_a_redirect_to_another_origin()
    {
        var file = await SeedSiteAsync(host, Acme);
        host.Web.Page(file.Origin + OwnershipProof.WellKnownPath, OwnershipProof.ExpectedValue(file.VerificationToken!) + "\n");
        var meta = await SeedSiteAsync(host, Acme);
        host.Web.Page(meta.Origin + "/", $"<html><head>{OwnershipProof.MetaTag(meta.VerificationToken!)}</head><body></body></html>");
        var redirected = await SeedSiteAsync(host, Acme);
        host.Web.Redirect(redirected.Origin + "/", "https://attacker.example.org/");
        host.Web.Page("https://attacker.example.org/", $"<html><head>{OwnershipProof.MetaTag(redirected.VerificationToken!)}</head></html>");

        await ActionAsync(host, SiteTypeId, "Site", "VerifySite", file.Id!, Ann);
        await ActionAsync(host, SiteTypeId, "Site", "VerifySite", meta.Id!, Ann);
        var (_, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "VerifySite", redirected.Id!, Ann);

        (await LoadDocumentAsync<Site>(host, file.Id!))!.VerificationMethod.Should().Be("WellKnownFile");
        (await LoadDocumentAsync<Site>(host, meta.Id!))!.VerificationMethod.Should().Be("MetaTag");
        body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("NoProof", raw);
        (await LoadDocumentAsync<Site>(host, redirected.Id!))!.IsVerified.Should().BeFalse("a proof served by another origin proves nothing");
    }

    [Fact]
    public async Task Without_proof_nothing_is_verified_and_the_caller_is_told_where_to_put_the_token()
    {
        var site = await SeedSiteAsync(host, Acme);

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "VerifySite", site.Id!, Ann);

        status.Should().Be(HttpStatusCode.OK, raw);
        body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("NoProof");
        raw.Should().Contain("_seotester." + site.Host);
        raw.Should().Contain(site.VerificationToken!);
        (await LoadDocumentAsync<Site>(host, site.Id!))!.ShouldSatisfy(s => !s.IsVerified && s.VerificationCheckedAtUtc != null);
    }

    [Fact]
    public async Task Only_an_Admin_verifies()
    {
        var site = await SeedSiteAsync(host, Acme);
        host.Dns.Txt[OwnershipProof.RecordName(site.Host)] = [OwnershipProof.ExpectedValue(site.VerificationToken!)];

        (await ActionAsync(host, SiteTypeId, "Site", "VerifySite", site.Id!, Max)).Status.Should().Be(HttpStatusCode.NotFound);
        (await ActionAsync(host, SiteTypeId, "Site", "VerifySite", site.Id!, Bob)).Status.Should().Be(HttpStatusCode.NotFound);
        (await LoadDocumentAsync<Site>(host, site.Id!))!.IsVerified.Should().BeFalse();
    }

    /// <summary>
    /// D18/D42: Globex holds the host verified. Ann proves control from Acme: Globex's Owner is asked by mail to let her
    /// in (a join request), and Acme's site stays unverified, so two organizations never both hold one site verified.
    /// </summary>
    [Fact]
    public async Task Verifying_a_site_another_organization_holds_sends_that_organization_a_join_request()
    {
        var held = await SeedSiteAsync(host, Globex, verified: true);
        var site = await SeedSiteAsync(host, Acme, hostName: held.Host);
        host.Dns.Txt[OwnershipProof.RecordName(site.Host)] = [OwnershipProof.ExpectedValue(site.VerificationToken!), OwnershipProof.ExpectedValue(held.VerificationToken!)];

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "VerifySite", site.Id!, Ann);

        status.Should().Be(HttpStatusCode.OK, raw);
        body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("ClaimRequested");
        (await LoadDocumentAsync<Site>(host, site.Id!))!.IsVerified.Should().BeFalse();
        using var session = host.Database.OpenAsyncSession();
        var requests = await session.Query<OrganizationInvitation>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(i => i.OrganizationId == Globex)
            .ToListAsync();
        requests.Should().Contain(i => i.Kind == OrganizationInvitationKind.JoinRequest && i.UserId == Ann);
        (await LogKindsAsync(site.Id!)).Should().Contain(AuditLogKinds.SiteClaimRequested);
    }

    [Fact]
    public async Task The_re_check_keeps_a_proven_site_and_withdraws_one_after_the_configured_failures()
    {
        var kept = await SeedSiteAsync(host, Acme, verified: true);
        var lost = await SeedSiteAsync(host, Acme, verified: true);
        host.Dns.Txt[OwnershipProof.RecordName(kept.Host)] = [OwnershipProof.ExpectedValue(kept.VerificationToken!)];
        await BackdateCheckAsync(kept.Id!, lost.Id!);

        var failures = await host.Factory.Services.GetRequiredService<MintPlayer.Spark.Settings.ISparkSettings>().GetAsync(SeoSettings.VerificationFailuresBeforeWithdrawal);
        for (var pass = 1; pass <= failures; pass++)
        {
            (await LoadDocumentAsync<Site>(host, lost.Id!))!.IsVerified.Should().BeTrue($"still verified before re-check {pass}");
            await RecheckAsync();
            await BackdateCheckAsync(kept.Id!, lost.Id!);
        }

        (await LoadDocumentAsync<Site>(host, kept.Id!))!.ShouldSatisfy(s => s.IsVerified && s.VerificationFailures == 0);
        (await LoadDocumentAsync<Site>(host, lost.Id!))!.ShouldSatisfy(s => !s.IsVerified && s.VerificationMethod == null);
        (await LogKindsAsync(lost.Id!)).Should().Contain(AuditLogKinds.VerificationWithdrawn);
    }

    private async Task RecheckAsync()
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SiteVerification>().RecheckDueAsync(batch: 1000, CancellationToken.None);
    }

    private async Task BackdateCheckAsync(params string[] siteIds)
    {
        using var session = host.Database.OpenAsyncSession();
        foreach (var id in siteIds)
            (await session.LoadAsync<Site>(id)).VerificationCheckedAtUtc = DateTime.UtcNow.AddDays(-30);
        session.Advanced.WaitForIndexesAfterSaveChanges();
        await session.SaveChangesAsync();
    }

    private async Task<List<string>> LogKindsAsync(string siteId)
    {
        using var session = host.Database.OpenAsyncSession();
        return (await session.Query<AuditLogEntry>().Customize(c => c.WaitForNonStaleResults()).Where(e => e.SiteId == siteId).ToListAsync()).Select(e => e.Kind).ToList();
    }
}
