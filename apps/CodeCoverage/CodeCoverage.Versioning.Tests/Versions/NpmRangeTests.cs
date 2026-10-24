using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Versions;

public class NpmRangeTests
{
    [Theory]
    [InlineData("^22.0.0", "22.4.1", true)]
    [InlineData("^22.0.0", "23.0.0", false)]
    [InlineData("^22.0.0", "21.9.9", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("^1.x", "1.9.0", true)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("~1", "1.9.0", true)]
    [InlineData("1.x", "1.4.0", true)]
    [InlineData("1.x", "2.0.0", false)]
    [InlineData("1.2", "1.2.7", true)]
    [InlineData("*", "9.9.9", true)]
    [InlineData("", "9.9.9", true)]
    [InlineData(">=1.0.0 <2", "1.99.0", true)]
    [InlineData(">=1.0.0 <2", "2.0.0", false)]
    [InlineData(">= 1.2 < 1.4", "1.3.5", true)]
    [InlineData(">1.2", "1.2.9", false)]
    [InlineData(">1.2", "1.3.0", true)]
    [InlineData("<=1.2", "1.2.9", true)]
    [InlineData("<=1.2", "1.3.0", false)]
    [InlineData("1.2.3 - 2", "2.9.0", true)]
    [InlineData("1.2.3 - 2.3.4", "2.3.5", false)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.2.4", false)]
    [InlineData("^17.0.0 || ^18.0.0", "18.2.0", true)]
    public void Ranges_admit_what_npm_admits(string range, string version, bool admitted)
    {
        NpmRange.TryParse(range, out var parsed).Should().BeTrue();
        parsed.Admits(version).Should().Be(admitted);
    }

    /// <summary>npm's rule: a prerelease only passes a set that names a prerelease of the same X.Y.Z.</summary>
    [Theory]
    [InlineData("^23.0.0", "23.0.0-next.3", false)]
    [InlineData("^22.0.0", "22.5.0-rc.1", false)]
    [InlineData("^22.0.0 || ^23.0.0-next.0", "23.0.0-next.3", true)]
    [InlineData("^23.0.0-next.0", "23.1.0-next.0", false)]
    [InlineData("^23.0.0-next.0", "23.1.0", true)]
    [InlineData("^22.0.0", "23.0.0-alpha.1", false)]
    public void A_prerelease_is_admitted_only_when_the_range_names_one(string range, string version, bool admitted)
    {
        NpmRange.TryParse(range, out var parsed).Should().BeTrue();
        parsed.Admits(version).Should().Be(admitted);
    }

    [Theory]
    [InlineData("workspace:*")]
    [InlineData("file:../lib")]
    [InlineData("npm:other@1")]
    [InlineData("latest")]
    [InlineData("1.x.3")]
    [InlineData("1 - 2 - 3")]
    public void Protocols_tags_and_garbage_are_not_ranges(string text)
        => NpmRange.TryParse(text, out _).Should().BeFalse();
}
