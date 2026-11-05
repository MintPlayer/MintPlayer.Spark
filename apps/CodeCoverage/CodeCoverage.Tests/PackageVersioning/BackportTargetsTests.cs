using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Releases;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#570 M8b, PRD D5 and D16: which backport labels a pull request gets, and the "Backport targets" check.</summary>
public class BackportTargetsTests
{
    private static LineSupport Line(string name, bool supported, bool current)
    {
        var line = ReleaseLine.TryParse(name)!;
        return new LineSupport(line, ReleaseLinePattern.Default.Format(line), supported, current, supported ? "supported" : "retired");
    }

    private static readonly LineSupport[] Lines =
    [
        Line("angular-22.x", true, true), Line("angular-21.x", true, false),
        Line("dotnet-11.x", true, true), Line("dotnet-10.x", true, false), Line("dotnet-9.x", false, false),
    ];

    [Fact]
    public void All_labels_every_supported_line_of_the_touched_ecosystems()
        => BackportTargets.Labels(Lines, ["dotnet"], BackportYml.All, isFix: false).Should().Equal(["backport: dotnet-11.x", "backport: dotnet-10.x"]);

    [Fact]
    public void Fixes_labels_older_lines_only_for_a_fix_but_the_current_line_always()
    {
        BackportTargets.Labels(Lines, ["dotnet", "angular"], BackportYml.Fixes, isFix: false).Should().Equal(["backport: angular-22.x", "backport: dotnet-11.x"]);
        BackportTargets.Labels(Lines, ["dotnet"], BackportYml.Fixes, isFix: true).Should().Equal(["backport: dotnet-11.x", "backport: dotnet-10.x"]);
    }

    [Fact]
    public void Off_labels_nothing_and_an_untouched_ecosystem_gets_no_label()
    {
        BackportTargets.Labels(Lines, ["dotnet"], BackportYml.Off, isFix: true).Should().BeEmpty();
        BackportTargets.Labels(Lines, [], BackportYml.All, isFix: true).Should().BeEmpty();
    }

    [Theory]
    [InlineData("fix: guard", true)]
    [InlineData("fix(core): guard", true)]
    [InlineData("fix!: guard", true)]
    [InlineData("Fix: guard", true)]
    [InlineData("feat: x", false)]
    [InlineData("fixture: tidy", false)]
    [InlineData("prefix: fix", false)]
    public void A_fix_is_a_conventional_fix_title(string title, bool fix)
        => BackportTargets.LooksLikeFix(title, [], ["bug"]).Should().Be(fix);

    [Fact]
    public void A_fix_label_makes_a_fix_too()
    {
        BackportTargets.LooksLikeFix("Guard against nulls", ["Bug"], ["bug"]).Should().BeTrue();
        BackportTargets.LooksLikeFix("Guard against nulls", ["enhancement"], ["bug"]).Should().BeFalse();
    }

    [Fact]
    public void The_check_lists_every_line_and_whether_it_applies_and_never_blocks()
    {
        var check = BackportTargets.Check(Lines, ["dotnet"], ["backport: dotnet-11.x", "size/S"], []);

        check.Name.Should().Be("Backport targets");
        (check.Completed, check.Conclusion).Should().Be((true, "neutral"));
        check.Title.Should().Be("2 lines apply: dotnet-11.x, dotnet-10.x");
        check.Text.Should().Contain("| `dotnet-11.x` | `release/dotnet-11.x` | yes, the current line | yes | yes |");
        check.Text.Should().Contain("| `dotnet-10.x` | `release/dotnet-10.x` | yes | yes | no |");
        check.Text.Should().Contain("| `dotnet-9.x` | `release/dotnet-9.x` | no: retired | yes | no |");
        check.Text.Should().Contain("| `angular-22.x` | `release/angular-22.x` | yes, the current line | no | no |");
    }

    [Fact]
    public void The_check_says_when_nothing_applies_and_names_the_sections_errors()
    {
        var check = BackportTargets.Check(Lines, [], [], ["backport.defaultLabels 'some' is not one of: all, fixes, off."]);

        check.Title.Should().Be("No line applies");
        check.Summary.Should().Contain("changes no package");
        check.Text.Should().Contain("backport.defaultLabels 'some'");
    }

    [Fact]
    public void The_check_of_a_repository_without_any_line_says_none_exists_yet()
    {
        var check = BackportTargets.Check([], ["dotnet"], [], []);

        check.Title.Should().Be("No line applies");
        check.Text.Should().Contain("| | no release line exists yet | | | |");
    }
}
