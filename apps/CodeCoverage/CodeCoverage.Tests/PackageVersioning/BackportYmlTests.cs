using CodeCoverage.PackageVersioning;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#570 M5, PRD §5.8: the <c>backport:</c> section, its D7 <c>onConflict</c> and D8 <c>markers</c>.</summary>
public class BackportYmlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("versioning:\n  mode: server\n")]
    public void An_absent_section_gives_the_defaults(string? yml)
    {
        var parsed = BackportYml.Parse(yml);

        parsed.Errors.Should().BeEmpty();
        parsed.Section.Should().Be(BackportSection.Default);
        parsed.Section.OnConflict.Should().Be(BackportYml.Comment, "D7: a conflict is commented on, nothing is pushed");
    }

    [Fact]
    public void The_section_is_read()
    {
        var parsed = BackportYml.Parse(
            "backport:\n  onConflict: draft\n  markers:\n    disable: [setup-action, TFM]\n    packages: [\"@mintplayer/ng-bootstrap\", \"MintPlayer.Spark*\"]\n");

        parsed.Errors.Should().BeEmpty();
        parsed.Section.OnConflict.Should().Be(BackportYml.Draft);
        parsed.Section.Markers.Disable.Should().BeEquivalentTo(["setup-action", "TFM"]);
        parsed.Section.Markers.Packages.Should().Equal(["@mintplayer/ng-bootstrap", "MintPlayer.Spark*"]);
    }

    [Fact]
    public void Lines_keep_or_retire_per_line_and_enqueue_defaults_to_true()
    {
        // #570 M8, D4; M7, D12.
        var parsed = BackportYml.Parse("backport:\n  lines:\n    dotnet-8.x: keep\n    angular-20.x: retire\n");

        parsed.Errors.Should().BeEmpty();
        parsed.Section.Lines.Should().BeEquivalentTo(new Dictionary<string, string> { ["dotnet-8.x"] = "keep", ["angular-20.x"] = "retire" });
        parsed.Section.Enqueue.Should().BeTrue();
        BackportYml.Parse("backport:\n  enqueue: false\n").Section.Enqueue.Should().BeFalse();
    }

    [Fact]
    public void Default_labels_are_all_unless_set_and_fixes_are_recognised_by_the_bug_label_by_default()
    {
        // #570 M8b, D5.
        BackportSection.Default.DefaultLabels.Should().Be(BackportYml.All);
        BackportSection.Default.FixLabels.Should().Equal(["bug"]);

        var parsed = BackportYml.Parse("backport:\n  defaultLabels: fixes\n  fixLabels: [bug, regression]\n");
        parsed.Errors.Should().BeEmpty();
        parsed.Section.DefaultLabels.Should().Be(BackportYml.Fixes);
        parsed.Section.FixLabels.Should().Equal(["bug", "regression"]);
        BackportYml.Parse("backport:\n  defaultLabels: off\n").Section.DefaultLabels.Should().Be(BackportYml.Off);
    }

    [Theory]
    [InlineData(null, BackportYml.Warn)]
    [InlineData("backport:\n  trialPick: refuse\n", BackportYml.Refuse)]
    [InlineData("backport:\n  trialPick: off\n", BackportYml.Off)]
    [InlineData("backport:\n  trialPick: warn\n", BackportYml.Warn)]
    public void The_trial_pick_warns_unless_set(string? yml, string expected)
    {
        // Segment G.
        var parsed = BackportYml.Parse(yml);

        parsed.Errors.Should().BeEmpty();
        parsed.Section.TrialPick.Should().Be(expected);
    }

    [Fact]
    public void A_misspelt_trial_pick_warns_and_never_refuses()
    {
        var parsed = BackportYml.Parse("backport:\n  trialPick: block\n");

        parsed.Errors.Should().ContainSingle().Which.Should().StartWith("backport.trialPick 'block'");
        parsed.Section.TrialPick.Should().Be(BackportYml.Warn);
    }

    [Fact]
    public void A_line_has_its_own_trial_pick_and_the_rest_take_the_default()
    {
        // Q10: "configurable per line, default warn".
        var parsed = BackportYml.Parse("backport:\n  trialPick: off\n  trialPicks:\n    dotnet-11.x: refuse\n");

        parsed.Errors.Should().BeEmpty();
        parsed.Section.TrialPickFor("dotnet-11.x").Should().Be(BackportYml.Refuse);
        parsed.Section.TrialPickFor("dotnet-10.x").Should().Be(BackportYml.Off);
    }

    [Theory]
    [InlineData("backport:\n  trialPicks:\n    \"10.x\": refuse\n", "backport.trialPicks '10.x'")]
    [InlineData("backport:\n  trialPicks:\n    dotnet-10.x: block\n", "backport.trialPicks.dotnet-10.x 'block'")]
    public void A_broken_line_trial_pick_says_why_and_warns(string yml, string error)
    {
        var parsed = BackportYml.Parse(yml);

        parsed.Errors.Should().ContainSingle().Which.Should().StartWith(error);
        parsed.Section.TrialPickFor("dotnet-10.x").Should().Be(BackportYml.Warn);
    }

    [Theory]
    [InlineData("backport:\n  defaultLabels: some\n", "backport.defaultLabels 'some'")]
    [InlineData("backport:\n  onConflict: push\n", "backport.onConflict 'push'")]
    [InlineData("backport:\n  markers:\n    disable: [docker]\n", "backport.markers.disable 'docker'")]
    [InlineData("backport:\n  markers:\n    packages: [\"*\"]\n", "backport.markers.packages '*'")]
    [InlineData("backport:\n  markers:\n    packages: [\"a b\"]\n", "backport.markers.packages 'a b'")]
    [InlineData("backport: [\n", "coverage.yml could not be read")]
    [InlineData("backport:\n  lines:\n    \"8.x\": keep\n", "backport.lines '8.x'")]
    [InlineData("backport:\n  lines:\n    dotnet-8.x: forever\n", "backport.lines.dotnet-8.x 'forever'")]
    public void A_broken_section_says_why_and_keeps_the_defaults(string yml, string error)
    {
        var parsed = BackportYml.Parse(yml);

        parsed.Errors.Should().ContainSingle().Which.Should().StartWith(error);
        parsed.Section.Markers.Should().Be(BackportSection.Default.Markers);
        parsed.Section.OnConflict.Should().Be(BackportYml.Comment);
    }
}
