using System.Net;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests;

/// <summary>
/// <c>/privacy</c> and <c>/terms</c> (<c>docs/seo_analyzer_PRD.md</c> D47): server-rendered like <c>/bot</c>, anonymous,
/// English or Dutch, with a version and an effective date. The controller's identity is configuration
/// (<c>SeoTester:Legal:*</c>); the test host sets none, so every such value shows the "to be completed" marker.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class LegalPagesTests(SeoTesterHostFixture host)
{
    private async Task<(HttpStatusCode Status, string? MediaType, string Html)> GetAsync(string url, string? acceptLanguage = null)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptLanguage is not null)
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anyone_reads_the_privacy_policy_in_English_with_what_is_kept_and_for_how_long()
    {
        var (status, type, html) = await GetAsync("/privacy");

        status.Should().Be(HttpStatusCode.OK);
        type.Should().Be("text/html");
        html.Should().Contain("<html lang=\"en\">");
        html.Should().Contain("Privacy policy");
        html.Should().Contain("Version 1");
        html.Should().Contain("Effective");
        html.Should().Contain("[to be completed]", "no SeoTester:Legal:* values are configured");
        html.Should().Contain("7 days", "page details and PDF reports");
        html.Should().Contain("90 days", "notifications");
        html.Should().Contain("365 days", "the abuse log, from the administrator's setting");
        html.Should().Contain("PageSpeed Insights");
        html.Should().Contain("No analytics");
        html.Should().Contain("Gegevensbeschermingsautoriteit");
        html.Should().NotContain("<app-root", "the page is the server's, not the SPA's");
        html.Should().NotContain("<script", "no JavaScript");
    }

    [Fact]
    public async Task Anyone_reads_the_terms_in_English()
    {
        var (status, type, html) = await GetAsync("/terms");

        status.Should().Be(HttpStatusCode.OK);
        type.Should().Be("text/html");
        html.Should().Contain("Terms of use");
        html.Should().Contain("Version 1");
        html.Should().Contain("robots.txt");
        html.Should().Contain("href=\"/bot\"");
        html.Should().Contain("as is");
        html.Should().Contain("Belgian law");
        html.Should().Contain("[to be completed]", "the court and the operator come from configuration");
        html.Should().NotContain("<script");
    }

    [Fact]
    public async Task A_browser_that_prefers_Dutch_reads_both_in_Dutch()
    {
        var privacy = await GetAsync("/privacy", "nl-BE,nl;q=0.9,en;q=0.5");
        privacy.Status.Should().Be(HttpStatusCode.OK);
        privacy.Html.Should().Contain("<html lang=\"nl\">");
        privacy.Html.Should().Contain("Privacyverklaring");
        privacy.Html.Should().Contain("[nog in te vullen]");
        privacy.Html.Should().Contain("90 dagen");

        var terms = await GetAsync("/terms?lang=nl");
        terms.Html.Should().Contain("<html lang=\"nl\">");
        terms.Html.Should().Contain("Gebruiksvoorwaarden");
        terms.Html.Should().Contain("[nog in te vullen]");
    }

    [Fact]
    public void Configured_values_replace_the_marker_and_are_encoded()
    {
        var legal = new SeoTester.Legal.SeoLegalOptions
        {
            OperatorName = "Example <Operator>", Address = "Street 1, 1000 City", ContactEmail = "privacy@example.com",
            EnterpriseNumber = "0000.000.000", Court = "the courts of Example", HostingProvider = "Example Hosting, Country",
        };
        var facts = new SeoTester.Legal.LegalPages.Facts(TimeSpan.FromDays(365), TimeSpan.FromHours(6));

        foreach (var language in new[] { "en", "nl" })
        {
            var privacy = SeoTester.Legal.LegalPages.RenderPrivacy(language, legal, facts);
            var terms = SeoTester.Legal.LegalPages.RenderTerms(language, legal);
            foreach (var html in new[] { privacy, terms })
            {
                html.Should().NotContain("data-to-be-completed", "every value is configured");
                html.Should().Contain("Example &lt;Operator&gt;", "values are HTML-encoded");
                html.Should().Contain("<a href=\"mailto:privacy@example.com\">privacy@example.com</a>");
            }
            privacy.Should().Contain("Example Hosting, Country");
            terms.Should().Contain("the courts of Example");
        }
        legal.Missing().Should().BeEmpty();
        // Only the required values count; the address, the enterprise number and the court never do.
        new SeoTester.Legal.SeoLegalOptions().Missing().Should().BeEquivalentTo(
            ["SeoTester:Legal:OperatorName", "SeoTester:Legal:ContactEmail", "SeoTester:Legal:HostingProvider"]);
    }

    [Fact]
    public void A_private_operator_without_address_or_enterprise_number_reads_cleanly_in_both_languages()
    {
        // The owner runs SeoTester as a private person (2026-10-11): no registered enterprise, so no address or KBO number.
        var legal = new SeoTester.Legal.SeoLegalOptions
        {
            OperatorName = "Example Person", ContactEmail = "privacy@example.com", HostingProvider = "Example Hosting, Country",
        };
        var facts = new SeoTester.Legal.LegalPages.Facts(TimeSpan.FromDays(365), TimeSpan.FromHours(6));

        var privacyEn = SeoTester.Legal.LegalPages.RenderPrivacy("en", legal, facts);
        var termsEn = SeoTester.Legal.LegalPages.RenderTerms("en", legal);
        var privacyNl = SeoTester.Legal.LegalPages.RenderPrivacy("nl", legal, facts);
        var termsNl = SeoTester.Legal.LegalPages.RenderTerms("nl", legal);

        foreach (var html in new[] { privacyEn, termsEn, privacyNl, termsNl })
        {
            html.Should().NotContain("data-to-be-completed", "every required value is set");
            html.Should().NotContain("enterprise number").And.NotContain("ondernemingsnummer");
            html.Should().NotContain("Example Person,", "nothing follows the name when the optional parts are empty");
        }
        privacyEn.Should().Contain("is operated by Example Person. The operator");
        termsEn.Should().Contain("It is operated by Example Person; contact:");
        privacyNl.Should().Contain("wordt uitgebaat door Example Person. De uitbater");
        termsEn.Should().Contain("disputes go to the competent Belgian courts,");
        termsNl.Should().Contain("geschillen gaan naar de bevoegde Belgische rechtbanken,");
        legal.Missing().Should().BeEmpty();
    }

    [Fact]
    public void A_registered_enterprise_shows_its_address_and_number_after_the_name()
    {
        var legal = new SeoTester.Legal.SeoLegalOptions
        {
            OperatorName = "Example BV", Address = "Street 1, 1000 City", EnterpriseNumber = "0123.456.789",
            ContactEmail = "privacy@example.com", HostingProvider = "Example Hosting, Country",
        };

        SeoTester.Legal.LegalPages.RenderTerms("en", legal)
            .Should().Contain("operated by Example BV, Street 1, 1000 City, enterprise number 0123.456.789; contact:");
        SeoTester.Legal.LegalPages.RenderTerms("nl", legal)
            .Should().Contain("uitgebaat door Example BV, Street 1, 1000 City, ondernemingsnummer 0123.456.789; contact:");
    }
}
