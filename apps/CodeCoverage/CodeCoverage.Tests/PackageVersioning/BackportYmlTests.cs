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

    [Theory]
    [InlineData("backport:\n  onConflict: push\n", "backport.onConflict 'push'")]
    [InlineData("backport:\n  markers:\n    disable: [docker]\n", "backport.markers.disable 'docker'")]
    [InlineData("backport:\n  markers:\n    packages: [\"*\"]\n", "backport.markers.packages '*'")]
    [InlineData("backport:\n  markers:\n    packages: [\"a b\"]\n", "backport.markers.packages 'a b'")]
    [InlineData("backport: [\n", "coverage.yml could not be read")]
    public void A_broken_section_says_why_and_keeps_the_defaults(string yml, string error)
    {
        var parsed = BackportYml.Parse(yml);

        parsed.Errors.Should().ContainSingle().Which.Should().StartWith(error);
        parsed.Section.Markers.Should().Be(BackportSection.Default.Markers);
        parsed.Section.OnConflict.Should().Be(BackportYml.Comment);
    }
}
