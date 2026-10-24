using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.PrSize;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>
/// The size rule (docs/coverage_pr_size_labels_PRD.md D5): the reference labeller's arithmetic on the default ignore
/// rules, with the repository's extra rules applied after the account's and the last matching rule deciding.
/// </summary>
public class PrSizeCalculatorTests
{
    private static PrSizeIgnoreRule Rule(string glob, EPrSizeIgnoreMode mode = EPrSizeIgnoreMode.Ignore)
        => new() { Id = glob, Glob = glob, Mode = mode };

    private static PrSizeCalculator Defaults(params PrSizeIgnoreRule[] repositoryRules)
        => new(PrSizeDefaults.IgnoreRules().Concat(repositoryRules));

    private static PrSizeResult Compute(PrSizeCalculator calculator, params ForgePullRequestFile[] files)
        => calculator.Compute(files, files.Sum(f => f.Additions), files.Sum(f => f.Deletions));

    [Fact]
    public void Reference_parity_on_the_default_rules()
    {
        var result = Compute(Defaults(),
            new("src/Program.cs", 40, 2),                      // counted: 42
            new("package-lock.json", 900, 300),                // ignored
            new("apps/web/package-lock.json", 50, 0),          // ignored, nested
            new("src/packages.lock.json", 20, 20),             // ignored
            new("Directory.Packages.props", 3, 1),             // ignored
            new("README.md", 100, 0),                          // ignored, at the root
            new("docs/guide.md", 10, 0),                       // ignored, nested
            new("src/appsettings.json", 30, 50),               // deletions only: 20
            new("CodeCoverage.Tests/FooTests.cs", 200, 5));    // deletions only: 0

        result.Size.Should().Be(62);
        result.UnlistedLines.Should().Be(0);
        result.Files.Single(f => f.Path == "src/appsettings.json").Outcome.Should().Be(PrSizeFileOutcome.DeletionsOnly);
        result.Files.Single(f => f.Path == "README.md").Outcome.Should().Be(PrSizeFileOutcome.Ignored);
    }

    [Fact]
    public void Net_deletions_floor_at_zero()
    {
        var result = Compute(Defaults(), new ForgePullRequestFile("data/seed.json", 500, 10));

        result.Size.Should().Be(0);
        result.Files.Single().Counted.Should().Be(0);
    }

    [Fact]
    public void Lockfiles_follow_the_json_rule_so_the_last_matching_rule_wins()
    {
        var calculator = Defaults();

        calculator.Classify("package-lock.json").Should().Be((PrSizeFileOutcome.Ignored, "**/package-lock.json"));
        calculator.Classify("tsconfig.json").Should().Be((PrSizeFileOutcome.DeletionsOnly, "**/*.json"));
    }

    [Fact]
    public void A_repository_rule_applies_after_the_account_rules()
    {
        var calculator = Defaults(Rule("generated/**"));

        calculator.Classify("generated/api.ts").Should().Be((PrSizeFileOutcome.Ignored, "generated/**"));
        calculator.Classify("src/api.ts").Should().Be((PrSizeFileOutcome.Counted, null));
    }

    [Fact]
    public void A_repository_bang_rule_counts_a_file_the_account_ignores()
    {
        var calculator = Defaults(Rule("!apps/web/**/*.json"));

        calculator.Classify("apps/web/src/i18n.json").Should().Be((PrSizeFileOutcome.Counted, "!apps/web/**/*.json"));
        calculator.Classify("apps/api/appsettings.json").Should().Be((PrSizeFileOutcome.DeletionsOnly, "**/*.json"));
    }

    [Fact]
    public void A_later_rule_overrides_a_bang_before_it()
    {
        var calculator = new PrSizeCalculator([Rule("!**/*.md"), Rule("docs/**")]);

        calculator.Classify("docs/a.md").Should().Be((PrSizeFileOutcome.Ignored, "docs/**"));
        calculator.Classify("README.md").Should().Be((PrSizeFileOutcome.Counted, "!**/*.md"));
    }

    [Fact]
    public void The_breakdown_names_the_matching_rule()
    {
        var result = Compute(Defaults(), new("src/a.cs", 1, 1), new("CHANGELOG.md", 5, 0));

        result.Files[0].Rule.Should().BeNull();
        result.Files[1].Rule.Should().Be("**/*.md");
    }

    [Fact]
    public void Lines_the_forge_did_not_list_count_fully()
    {
        // GitHub lists at most 3 000 files; the PR's totals hold the rest.
        var result = Defaults().Compute([new ForgePullRequestFile("README.md", 10, 0)], totalAdditions: 510, totalDeletions: 40);

        result.Size.Should().Be(540);
        result.UnlistedLines.Should().Be(540);
    }

    [Fact]
    public void Above_the_file_cap_the_totals_are_the_size()
    {
        var result = PrSizeCalculator.FromTotals(12_000, 3_000);

        result.Size.Should().Be(15_000);
        result.Files.Should().BeEmpty();
    }
}
