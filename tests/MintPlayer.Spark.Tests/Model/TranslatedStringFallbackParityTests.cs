using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Driven by the same fixture as the TypeScript <c>translated-string.spec.ts</c> (commerce X17): the server's
/// <see cref="TranslatedString.Resolve"/> and the client's <c>resolveTranslation</c> pick the same text.
/// </summary>
/// <remarks>
/// One file, two implementations, on purpose. Before X17 they had already drifted: the server stripped only
/// the first subtag and ended at "the first value", while the client had no base-language step and tried
/// <c>en</c> before the first value. Each side had its own tests, so nothing noticed.
/// </remarks>
public class TranslatedStringFallbackParityTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "libs", "node_packages", "ng-spark", "models", "src", "translated-string.fixture.json");

    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Every_fixture_range_has_its_RFC_4647_lookup_chain()
    {
        var chains = Fixture().GetProperty("chains").EnumerateArray().ToList();
        chains.Should().NotBeEmpty("the fixture must actually load — a silently empty file would make this test vacuous");

        foreach (var c in chains)
        {
            var range = c.GetProperty("range").GetString();
            var expected = c.GetProperty("chain").EnumerateArray().Select(e => e.GetString()!).ToList();
            LanguageTag.LookupChain(range).ToList().Should().Equal(expected, "the chain of '{0}'", range);
        }
    }

    [Fact]
    public void Every_fixture_case_resolves_to_its_expected_text()
    {
        var cases = Fixture().GetProperty("cases").EnumerateArray().ToList();
        cases.Should().NotBeEmpty("the fixture must actually load — a silently empty file would make this test vacuous");

        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString();
            var value = c.GetProperty("value").Deserialize<TranslatedString>()!;
            var ranges = c.GetProperty("ranges").EnumerateArray().Select(e => e.GetString()).ToList();
            // Absent means the default ("en"); an explicit null means none at all.
            var defaultLanguage = c.TryGetProperty("defaultLanguage", out var d)
                ? (d.ValueKind == JsonValueKind.Null ? null : d.GetString())
                : TranslatedString.DefaultLanguage;
            DateOnly? asOf = c.TryGetProperty("asOf", out var a) ? DateOnly.Parse(a.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;

            value.Resolve(ranges, defaultLanguage, asOf).Should().Be(c.GetProperty("expected").GetString(), "case '{0}'", name);
        }
    }
}
