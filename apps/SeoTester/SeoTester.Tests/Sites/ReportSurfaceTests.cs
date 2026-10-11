using System.Net;
using System.Text.Json;
using SeoTester.Entities;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Sites;

/// <summary>
/// M11's server surfaces for the pages: the public catalogue texts the report pages show (F5), the site badge on a
/// share link (F11, D31) and the operator dashboard (F16, D28).
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class ReportSurfaceTests(SeoTesterHostFixture host)
{
    private const string Administrators = "Administrators";
    private const string OperatorDashboardTypeId = "458035ba-1fc4-4f98-b24c-de1edbf8214c";

    [Fact]
    public async Task The_catalogue_texts_are_public_in_the_requested_language()
    {
        using var client = host.CreateClient();

        var en = JsonDocument.Parse(await client.GetStringAsync("/seotester/catalogue?culture=en")).RootElement;
        var nl = JsonDocument.Parse(await client.GetStringAsync("/seotester/catalogue?culture=nl")).RootElement;

        en.GetProperty("culture").GetString().Should().Be("en");
        nl.GetProperty("culture").GetString().Should().Be("nl");
        en.GetProperty("checks").GetArrayLength().Should().BeGreaterThan(100, "the whole catalogue");
        var check = Check(en, "seo.title-present");
        var dutch = Check(nl, "seo.title-present");
        check.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        dutch.GetProperty("title").GetString().Should().NotBe(check.GetProperty("title").GetString(), "the Dutch title");
        check.GetProperty("category").GetString().Should().Be("Seo");
        Label(nl, "categories", "Accessibility").Should().Be("Toegankelijkheid");
        Label(en, "severities", "Critical").Should().Be("Critical");
        en.GetProperty("checks").EnumerateArray().Should().Contain(c => c.GetProperty("snippets").ValueKind == JsonValueKind.Object, "platform snippets travel");

        static JsonElement Check(JsonElement catalogue, string id) => catalogue.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        static string? Label(JsonElement catalogue, string list, string key)
            => catalogue.GetProperty(list).EnumerateArray().Single(c => c.GetProperty("key").GetString() == key).GetProperty("label").GetString();
    }

    [Fact]
    public async Task An_Admin_makes_a_badge_that_lives_and_dies_with_the_share_link()
    {
        var site = await SeedSiteAsync(host, Acme);
        using (var session = host.Database.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<Site>(site.Id);
            stored.LatestScore = 87;
            stored.LatestGrade = "B";
            await session.SaveChangesAsync();
        }

        (await ActionAsync(host, SiteTypeId, "Site", "ShareBadge", site.Id!, Max)).Raw.Should().NotContain("badge.svg", "D31: Owners and Admins");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "ShareBadge", site.Id!, Ann);
        status.Should().Be(HttpStatusCode.OK, raw);
        var markdown = Strings(body).Single(s => s.Contains("badge.svg"));
        var badge = new Uri(markdown[(markdown.IndexOf("](", StringComparison.Ordinal) + 2)..markdown.IndexOf(")](", StringComparison.Ordinal)]);
        markdown.Should().Contain("/shared/Site/", "the badge links to the shared report");

        using var client = host.CreateClient();
        var svg = await client.GetAsync(badge.PathAndQuery);
        svg.StatusCode.Should().Be(HttpStatusCode.OK);
        svg.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        (await svg.Content.ReadAsStringAsync()).Should().Contain("87 B").And.Contain("#1a7f37");

        (await client.GetAsync(badge.PathAndQuery.Replace("token=", "token=x"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/seotester/badge.svg?site={Uri.EscapeDataString(site.Id!)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ActionAsync(host, SiteTypeId, "Site", "RevokeShareLink", site.Id!, Ann)).Status.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(badge.PathAndQuery)).StatusCode.Should().Be(HttpStatusCode.NotFound, "revoking the link ends the badge");
    }

    [Fact]
    public async Task Only_administrators_open_the_operator_dashboard_and_its_figures()
    {
        var site = await SeedSiteAsync(host, Acme);

        var page = await LoadAsync(host, OperatorDashboardTypeId, "main", Ann, Administrators);
        page.Status.Should().Be(HttpStatusCode.OK, page.Raw);
        (await LoadAsync(host, OperatorDashboardTypeId, "main", Ann)).Status.Should().Be(HttpStatusCode.NotFound);

        var (adminStatus, admin) = await GetAsync("/seotester/admin/dashboard", Ann, Administrators);
        adminStatus.Should().Be(HttpStatusCode.OK);
        admin.GetProperty("counts").GetProperty("sites").GetInt32().Should().BeGreaterThan(0);
        admin.GetProperty("database").GetProperty("collections").EnumerateArray().Should().Contain(c => c.GetProperty("name").GetString() == "Sites");
        admin.GetProperty("database").GetProperty("sizeOnDiskBytes").GetInt64().Should().BeGreaterThan(0);
        admin.GetProperty("sites").EnumerateArray().Should().Contain(s => s.GetProperty("origin").GetString() == site.Origin && s.GetProperty("organization").GetString() == "Acme",
            "every organization's sites, with the organization's name (D28)");

        (await GetAsync("/seotester/admin/dashboard", Ann)).Status.Should().Be(HttpStatusCode.NotFound, "a signed-in non-administrator");
        (await GetAsync("/seotester/admin/dashboard", null)).Status.Should().Be(HttpStatusCode.NotFound, "anonymous");
    }

    /// <summary>A finished run offers no Cancel; only a completed one offers Download PDF.</summary>
    [Theory]
    [InlineData("Running", new[] { "ExportReport" })]
    [InlineData("Completed", new[] { "CancelAudit" })]
    [InlineData("Blocked", new[] { "CancelAudit", "ExportReport" })]
    public async Task A_run_offers_only_the_actions_its_state_allows(string status, string[] disabled)
    {
        var site = await SeedSiteAsync(host, Acme);
        var run = new AuditRun { SiteId = site.Id, OrganizationId = Acme, Origin = site.Origin, Status = status, StartedAtUtc = DateTime.UtcNow };
        using (var session = host.Database.OpenAsyncSession())
        {
            await session.StoreAsync(run);
            await session.SaveChangesAsync();
        }

        var (loaded, body, raw) = await LoadAsync(host, AuditRunTypeId, run.Id!, Ann);

        loaded.Should().Be(HttpStatusCode.OK, raw);
        var actual = body.TryGetProperty("disabledActions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(a => a.GetString()!).Where(a => a is "CancelAudit" or "ExportReport").OrderBy(a => a).ToArray()
            : [];
        actual.Should().Equal(disabled);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, string? user, params string[] roles)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        SeoRequests.AddIdentity(request, user, roles);
        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? JsonDocument.Parse(raw).RootElement.Clone() : default);
    }

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}
