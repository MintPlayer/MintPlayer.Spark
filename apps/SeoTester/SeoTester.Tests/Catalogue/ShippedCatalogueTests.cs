using System.Text.RegularExpressions;
using SeoTester.Catalogue;

namespace SeoTester.Tests.Catalogue;

/// <summary>
/// The shipped catalogue (<c>SeoTester.Library/Catalogue/checks.json</c>, PRD §3.1, D16, D32) is a valid seed: every
/// check has a well-formed id in its category, the research's weight for its severity, en and nl texts with the same
/// placeholders, known snippet platforms; and a malformed file is refused rather than half-seeded.
/// </summary>
public partial class ShippedCatalogueTests
{
    [Fact]
    public void Every_shipped_check_is_well_formed_and_every_category_is_covered()
    {
        var checks = ShippedCatalogue.All;

        checks.Count.Should().BeGreaterThan(80, "research §1 lists about 90 checks");
        checks.Select(c => c.Category).Distinct().Count().Should().Be(Enum.GetValues<CheckCategory>().Length);
        foreach (var check in checks)
        {
            ShippedCatalogue.IsCheckId(check.Id).Should().BeTrue(check.Id);
            check.Id.Split('.')[0].Should().Be(check.Category.ToString().ToLowerInvariant(), check.Id);
            check.Weight.Should().Be(CheckSeverityDefaults.Weight(check.Severity), check.Id);
            foreach (var text in new[] { check.Title.En, check.Title.Nl, check.Summary.En, check.Summary.Nl, check.HowToFix.En, check.HowToFix.Nl })
                text.Should().NotBeNullOrWhiteSpace(check.Id);
            Placeholders(check.Summary.En).Should().Equal(Placeholders(check.Summary.Nl), check.Id);
            foreach (var platform in check.Snippets.Keys)
                new[] { "nginx", "apache", "iis", "html" }.Should().Contain(platform, check.Id);
        }
    }

    [Fact]
    public void A_catalogue_with_a_duplicate_or_malformed_id_is_refused()
    {
        const string one = """{ "id": "seo.title", "category": "Seo", "severity": "High", "weight": 6, "title": { "en": "a", "nl": "b" } }""";

        var duplicate = () => ShippedCatalogue.Parse($$"""{ "version": 1, "checks": [ {{one}}, {{one}} ] }""");
        var malformed = () => ShippedCatalogue.Parse("""{ "version": 1, "checks": [ { "id": "Title Present", "category": "Seo", "severity": "High", "weight": 6 } ] }""");
        var unknownSeverity = () => ShippedCatalogue.Parse("""{ "version": 1, "checks": [ { "id": "seo.x", "category": "Seo", "severity": "Severe", "weight": 6 } ] }""");

        duplicate.Should().Throw<InvalidDataException>();
        malformed.Should().Throw<InvalidDataException>();
        unknownSeverity.Should().Throw<System.Text.Json.JsonException>();
        ShippedCatalogue.Parse($$"""{ "version": 1, "checks": [ {{one}} ] }""").Single().Id.Should().Be("seo.title");
    }

    [Fact]
    public void The_content_hash_follows_every_field_and_ignores_snippet_order()
    {
        ShippedCheck Make(Action<ShippedCheck>? change = null)
        {
            var check = new ShippedCheck { Id = "seo.x", Category = CheckCategory.Seo, Severity = CheckSeverity.High, Weight = 6, Title = new() { En = "a", Nl = "b" } };
            check.Snippets["html"] = "<x>";
            check.Snippets["nginx"] = "y;";
            change?.Invoke(check);
            return check;
        }

        var baseline = Make().ContentHash();
        Make(c => { c.Snippets.Clear(); c.Snippets["nginx"] = "y;"; c.Snippets["html"] = "<x>"; }).ContentHash().Should().Be(baseline);
        Make(c => c.Title.Nl = "c").ContentHash().Should().NotBe(baseline);
        Make(c => c.Weight = 5).ContentHash().Should().NotBe(baseline);
        Make(c => c.Enabled = false).ContentHash().Should().NotBe(baseline);
        Make(c => c.DocsUrl = "https://example.com/").ContentHash().Should().NotBe(baseline);
    }

    private static List<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v).ToList();

    [GeneratedRegex(@"\{[a-z]+\}")]
    private static partial Regex PlaceholderPattern();
}
