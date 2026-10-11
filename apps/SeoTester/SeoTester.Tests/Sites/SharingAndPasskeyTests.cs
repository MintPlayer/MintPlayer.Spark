using System.Net;
using System.Text.Json;
using System.Web;
using MintPlayer.Spark.Testing;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Sites;

/// <summary>
/// Share links of sites (X6, D11, D31): Owners and Admins share, a link shows the declared allow-list, and the
/// administrator's <c>SeoTester:Features:Sharing</c> gate decides per site. Passkeys (D46): on for signed-in users.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class SharingAndPasskeyTests(SeoTesterHostFixture host)
{
    [Fact]
    public async Task An_Admin_shares_a_site_and_the_link_shows_only_the_declared_attributes()
    {
        var site = await SeedSiteAsync(host, Acme);

        (await ActionAsync(host, SiteTypeId, "Site", "ShareLink", site.Id!, Max)).Status.Should().Be(HttpStatusCode.NotFound, "D31: Owners and Admins");
        var link = await ShareAsync(site.Id!);
        var shared = await PostAsync(host, "/spark/share/load", link, null);

        shared.Status.Should().Be(HttpStatusCode.OK, shared.Raw);
        shared.Body.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString())
            .OrderBy(n => n).Should().Equal("IsVerified", "LatestGrade", "LatestRunAtUtc", "LatestScore", "Origin");
        shared.Raw.Should().NotContain(site.VerificationToken!);
        shared.Body.GetProperty("queries").TryGetProperty("site-runs", out _).Should().BeTrue();
    }

    /// <summary>VerifiedOnly hides an unverified site's link and refuses a new one; verifying the site brings the same link back.</summary>
    [Fact]
    public async Task Under_VerifiedOnly_an_unverified_sites_link_stops_and_comes_back_once_verified()
    {
        var site = await SeedSiteAsync(host, Acme);
        var link = await ShareAsync(site.Id!);

        await SetSettingAsync(host, SeoSettings.Sharing.Key, nameof(SeoFeatureScope.VerifiedOnly));
        try
        {
            (await PostAsync(host, "/spark/share/load", link, null)).Status.Should().Be(HttpStatusCode.NotFound);
            (await ActionAsync(host, SiteTypeId, "Site", "ShareLink", site.Id!, Ann)).Raw.Should().NotContain("/shared/");

            using (var session = host.Database.OpenAsyncSession())
            {
                (await session.LoadAsync<SeoTester.Entities.Site>(site.Id)).IsVerified = true;
                await session.SaveChangesAsync();
            }
            (await PostAsync(host, "/spark/share/load", link, null)).Status.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await SetSettingAsync(host, SeoSettings.Sharing.Key, null);
        }
    }

    [Fact]
    public async Task Passkeys_are_on_and_a_signed_in_user_reaches_their_passkeys_page()
    {
        const string PasskeysTypeId = "cd8a0767-2a18-5c73-a608-14cf9efe99ac";
        using var client = host.CreateClient();

        var capabilities = JsonDocument.Parse(await client.GetStringAsync("/spark/auth/capabilities")).RootElement;
        var options = await PostAsync(host, "/spark/auth/passkeys/request-options", new { }, null);
        var page = await LoadAsync(host, PasskeysTypeId, "me", Ann);
        var anonymousPage = await LoadAsync(host, PasskeysTypeId, "me", null);

        capabilities.GetProperty("passkeys").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("localCredentials").GetString().Should().Be("Disabled", "a passkey is no password (D2 stands for passwords)");
        options.Status.Should().Be(HttpStatusCode.OK, options.Raw);
        options.Raw.Should().Contain("\"rpId\":\"localhost\"");
        page.Status.Should().Be(HttpStatusCode.OK, page.Raw);
        anonymousPage.Status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    private async Task<object> ShareAsync(string siteId)
    {
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "ShareLink", siteId, Ann);
        status.Should().Be(HttpStatusCode.OK, raw);
        // The link travels once, in the showSecret client operation.
        var uri = new Uri(Flatten(body).First(s => s.Contains("/shared/")));
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new { type = segments[1], token = Uri.UnescapeDataString(segments[2]), id = HttpUtility.ParseQueryString(uri.Query)["id"] };

        static IEnumerable<string> Flatten(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => [e.GetString()!],
            JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Flatten(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().SelectMany(Flatten),
            _ => [],
        };
    }
}
