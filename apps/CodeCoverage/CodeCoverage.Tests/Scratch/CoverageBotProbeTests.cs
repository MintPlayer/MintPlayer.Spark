using CodeCoverage.Scratch;
using Xunit;

namespace CodeCoverage.Tests.Scratch;

/// <summary>
/// THROWAWAY — do not merge. Covers only part of <see cref="CoverageBotProbe"/>
/// on purpose, so the dogfood PR's patch coverage lands below 100% and the
/// sticky comment has a real number to show.
/// </summary>
public class CoverageBotProbeTests
{
    [Theory]
    [InlineData(100, "excellent")]
    [InlineData(90, "excellent")]
    [InlineData(80, "good")]
    [InlineData(75, "good")]
    [InlineData(60, "fair")]
    [InlineData(50, "fair")]
    [InlineData(10, "poor")]
    public void Band_names_each_range(double percent, string expected)
        => CoverageBotProbe.Band(percent).Should().Be(expected);

    [Fact]
    public void Ratio_is_a_percentage()
    {
        CoverageBotProbe.Ratio(487, 1000).Should().Be(48.7);
        CoverageBotProbe.Ratio(1, 1).Should().Be(100);
    }

    [Fact]
    public void Ratio_treats_nothing_measurable_as_zero()
        => CoverageBotProbe.Ratio(0, 0).Should().Be(0);

    // Describe() and Trend() are intentionally left uncovered.
}
