using FluentAssertions;
using MintPlayer.Spark.Services;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Driven by the same fixture as the TypeScript <c>selection-rule.spec.ts</c>.
/// </summary>
/// <remarks>
/// One file, two parsers, on purpose. Vidyano — where this grammar comes from — has the same
/// algorithm in C# and JavaScript, and they have already drifted: on <c>"=abc"</c> the C# throws
/// while the JS silently permits every selection. Nothing caught that, because each port had its
/// own tests. A shared fixture is the only thing that makes the two provably agree, and the
/// consequence of disagreement here is a button that looks enabled and a server that answers 400.
/// </remarks>
public class SelectionRuleParserTests
{
    private sealed record FixtureCase(string? Rule, bool Valid, bool[]? Expected, string? Why);

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "libs", "node_packages", "ng-spark", "models", "src", "selection-rule.fixture.json");

    private static (int[] Counts, List<FixtureCase> Cases) LoadFixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var counts = doc.RootElement.GetProperty("counts").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new FixtureCase(
            c.TryGetProperty("rule", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() : null,
            !c.TryGetProperty("valid", out var v) || v.GetBoolean(),
            c.TryGetProperty("expected", out var e) ? e.EnumerateArray().Select(x => x.GetBoolean()).ToArray() : null,
            c.TryGetProperty("why", out var w) ? w.GetString() : null)).ToList();
        return (counts, cases);
    }

    [Fact]
    public void Every_wellformed_fixture_rule_matches_its_expected_row()
    {
        var (counts, cases) = LoadFixture();
        cases.Should().NotBeEmpty("the fixture must actually load — a silently empty file would make this test vacuous");

        foreach (var c in cases.Where(c => c.Valid))
        {
            var predicate = SelectionRuleParser.Parse(c.Rule);
            for (var i = 0; i < counts.Length; i++)
            {
                predicate(counts[i]).Should().Be(c.Expected![i],
                    "rule '{0}' with {1} selected{2}", c.Rule ?? "(null)", counts[i], c.Why is null ? "" : $" — {c.Why}");
            }
        }
    }

    [Fact]
    public void Every_malformed_fixture_rule_is_rejected_rather_than_permitting_everything()
    {
        var (_, cases) = LoadFixture();
        var malformed = cases.Where(c => !c.Valid).ToList();
        malformed.Should().NotBeEmpty();

        foreach (var c in malformed)
        {
            SelectionRuleParser.IsValid(c.Rule).Should().BeFalse(
                "'{0}' is malformed and Vidyano's fallback would silently permit every selection{1}",
                c.Rule, c.Why is null ? "" : $" — {c.Why}");

            var parse = () => SelectionRuleParser.Parse(c.Rule);
            parse.Should().Throw<FormatException>();
        }
    }

    [Fact]
    public void An_absent_rule_imposes_no_requirement()
    {
        // Resolves a contradiction in Spark's own docs: the guide said "no requirement",
        // the older PRD said it defaults to "=0". The guide wins — "=0" would break every
        // action that omits the field, which is all of them but one.
        SelectionRuleParser.Parse(null)(0).Should().BeTrue();
        SelectionRuleParser.Parse(null)(7).Should().BeTrue();
        SelectionRuleParser.IsValid(null).Should().BeTrue();
    }

    [Fact]
    public void Repeated_parses_of_the_same_rule_agree()
    {
        // The parser caches compiled predicates across threads.
        var results = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => SelectionRuleParser.Parse("1<X<5")(3))
            .Distinct()
            .ToList();

        results.Should().ContainSingle().Which.Should().BeTrue();
    }
}
