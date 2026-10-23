using CodeCoverage.Entities;
using CodeCoverage.PrSize;
using MintPlayer.Spark.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>
/// The save-time rules (docs/coverage_pr_size_labels_PRD.md D2, D5): what GitHub would refuse, or what would leave a pull
/// request unlabelled, is refused with the offending cell named, so the inline list shows it on that row.
/// </summary>
public class PrSizeValidationTests
{
    private static Account Account(Action<Account>? change = null)
    {
        var account = new Account { Login = "acme" }.ApplyPrSizeDefaults();
        change?.Invoke(account);
        return account;
    }

    private static SparkValidationException Refused(Account account)
        => Record.Exception(() => PrSizeValidation.ValidateAccount(account)).Should().BeOfType<SparkValidationException>().Which;

    [Fact]
    public void The_defaults_are_valid()
        => PrSizeValidation.ValidateAccount(Account(a => a.PrSizeLabels = true));

    [Fact]
    public void A_duplicate_name_is_refused_case_insensitively()
    {
        var error = Refused(Account(a => a.PrSizeLabelScale[2].Name = "SIZE/s"));

        error.AttributeName.Should().Be("PrSizeLabelScale[2].Name");
    }

    [Fact]
    public void A_duplicate_threshold_is_refused()
    {
        var error = Refused(Account(a => a.PrSizeLabelScale[3].MinLines = 30));

        error.AttributeName.Should().Be("PrSizeLabelScale[3].MinLines");
    }

    [Fact]
    public void A_scale_without_a_zero_row_is_refused_on_the_smallest_row()
    {
        var error = Refused(Account(a => a.PrSizeLabelScale[0].MinLines = 5));

        error.AttributeName.Should().Be("PrSizeLabelScale[0].MinLines");
    }

    [Fact]
    public void A_description_over_100_characters_is_refused()
    {
        var error = Refused(Account(a => a.PrSizeLabelScale[1].Description = new string('d', 101)));

        error.AttributeName.Should().Be("PrSizeLabelScale[1].Description");
    }

    [Fact]
    public void A_description_of_100_characters_is_accepted()
        => PrSizeValidation.ValidateAccount(Account(a => a.PrSizeLabelScale[1].Description = new string('d', 100)));

    [Fact]
    public void A_name_over_50_characters_is_refused()
    {
        var error = Refused(Account(a => a.PrSizeLabelScale[4].Name = new string('n', 51)));

        error.AttributeName.Should().Be("PrSizeLabelScale[4].Name");
    }

    [Fact]
    public void An_empty_name_is_refused()
        => Refused(Account(a => a.PrSizeLabelScale[5].Name = " ")).AttributeName.Should().Be("PrSizeLabelScale[5].Name");

    [Fact]
    public void Enabling_with_an_empty_scale_is_refused()
    {
        var error = Refused(Account(a => { a.PrSizeLabels = true; a.PrSizeLabelScale.Clear(); }));

        error.AttributeName.Should().Be(nameof(Entities.Account.PrSizeLabelScale));
    }

    [Fact]
    public void An_empty_scale_is_allowed_while_switched_off()
        => PrSizeValidation.ValidateAccount(Account(a => a.PrSizeLabelScale.Clear()));

    [Theory]
    [InlineData("")]
    [InlineData("!")]
    [InlineData("  ")]
    public void An_empty_glob_is_refused(string glob)
    {
        var error = Refused(Account(a => a.PrSizeIgnoreRules[1].Glob = glob));

        error.AttributeName.Should().Be("PrSizeIgnoreRules[1].Glob");
    }

    [Fact]
    public void A_repository_rule_is_checked_too()
    {
        var repository = new Repository { PrSizeIgnoreRules = [new() { Id = "a", Glob = "!" }] };

        var error = Record.Exception(() => PrSizeValidation.ValidateRepository(repository))
            .Should().BeOfType<SparkValidationException>().Which;

        error.AttributeName.Should().Be("PrSizeIgnoreRules[0].Glob");
    }
}
