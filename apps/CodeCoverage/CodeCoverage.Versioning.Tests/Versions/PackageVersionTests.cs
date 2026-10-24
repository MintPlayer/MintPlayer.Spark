using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Versions;

public class PackageVersionTests
{
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("13.2.0")]
    [InlineData("11.0.0-preview.3")]
    [InlineData("22.1.0-next.12")]
    [InlineData("1.0.0-alpha.0")]
    [InlineData("4.5.6-rc.1")]
    public void Our_versions_round_trip(string text)
        => PackageVersion.Parse(text).ToString().Should().Be(text);

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-canary.1")]
    [InlineData("1.0.0-preview")]
    [InlineData("1.0.0-preview.1.2")]
    [InlineData("1.0.0+build")]
    [InlineData("1.0.0\n")]
    [InlineData("١.0.0")]
    [InlineData("99999999999.0.0")]
    [InlineData("")]
    public void Anything_else_is_refused(string text)
        => PackageVersion.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Versions_sort_by_core_then_maturity_then_counter()
    {
        string[] sorted = ["1.0.0-alpha.2", "1.0.0-beta.1", "1.0.0-next.1", "1.0.0-preview.1", "1.0.0-preview.10", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0-alpha.1", "2.0.0"];
        Enumerable.Reverse(sorted).Select(PackageVersion.Parse).Order().Select(v => v.ToString()).Should().Equal(sorted);
    }

    [Theory]
    [InlineData("11.0.0-preview.7.24407.12", PrereleaseLabel.Preview)]
    [InlineData("22.0.0-next.3", PrereleaseLabel.Next)]
    [InlineData("19.0.0-rc.1", PrereleaseLabel.Rc)]
    [InlineData("1.0.0-RC1", PrereleaseLabel.Rc)]
    [InlineData("3.5.0-beta.2", PrereleaseLabel.Beta)]
    [InlineData("19.0.0-canary-ab12cd", PrereleaseLabel.Alpha)]
    [InlineData("5.0.0-dev.20260101", PrereleaseLabel.Alpha)]
    public void Upstream_labels_are_mirrored_and_unknown_ones_count_as_alpha(string upstream, PrereleaseLabel expected)
        => Labels.LeastMature([upstream]).Should().Be(expected);

    [Fact]
    public void The_least_mature_upstream_label_wins()
    {
        Labels.LeastMature(["22.0.0-rc.1", "8.0.0", "11.0.0-preview.2"]).Should().Be(PrereleaseLabel.Preview);
        Labels.LeastMature(["22.0.0", "8.0.0", "not a version"]).HasValue.Should().BeFalse();
    }
}
