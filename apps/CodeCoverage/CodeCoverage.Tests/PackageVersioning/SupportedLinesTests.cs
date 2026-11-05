using CodeCoverage.Entities;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Releases;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#570 M8, PRD D4 and D16: which release lines are supported.</summary>
public class SupportedLinesTests
{
    private static readonly ReleaseLinePattern Pattern = ReleaseLinePattern.Default;

    private static PlatformRelease Release(string platform, int major, string phase)
        => new() { Id = PlatformRelease.IdFor(platform, major), Platform = platform, Major = major, Phase = phase };

    private static readonly PlatformRelease[] Releases =
    [
        Release("dotnet", 8, "maintenance"), Release("dotnet", 9, "eol"), Release("dotnet", 10, "active"), Release("dotnet", 11, "preview"),
        Release("angular", 20, "eol"), Release("angular", 21, "maintenance"), Release("angular", 22, "active"),
    ];

    private static readonly string[] Branches =
    [
        "master", "release/dotnet-8.x", "release/dotnet-9.x", "release/dotnet-10.x", "release/angular-20.x", "release/angular-22.x",
        "release/php-3.x", "release/php-4.x", "release/notes",
    ];

    private static Dictionary<string, LineSupport> Evaluate(IReadOnlyDictionary<string, string>? overrides = null)
        => SupportedLines.Evaluate(Pattern, Branches, Releases, overrides ?? new Dictionary<string, string>()).ToDictionary(l => l.Line.ToString());

    [Fact]
    public void An_older_line_whose_major_the_lifecycle_data_lacks_is_not_supported_unless_kept()
    {
        var line = SupportedLines.Evaluate(Pattern, ["release/dotnet-7.x", "release/dotnet-10.x"], Releases, new Dictionary<string, string>())
            .Single(l => l.Line.ToString() == "dotnet-7.x");

        (line.Supported, line.Current).Should().Be((false, false));
        line.Reason.Should().Be("dotnet 7 is not in the lifecycle data; `keep` the line to backport to it");
    }

    [Fact]
    public void A_line_is_supported_while_its_platform_is_and_the_newest_line_of_an_ecosystem_always_is()
    {
        var lines = Evaluate();

        lines.Keys.Should().BeEquivalentTo(["dotnet-8.x", "dotnet-9.x", "dotnet-10.x", "angular-20.x", "angular-22.x", "php-3.x", "php-4.x"]);
        (lines["dotnet-8.x"].Supported, lines["dotnet-8.x"].Current, lines["dotnet-8.x"].Branch).Should().Be((true, false, "release/dotnet-8.x"));
        lines["dotnet-9.x"].Supported.Should().BeFalse("its platform reached end of life");
        (lines["dotnet-10.x"].Supported, lines["dotnet-10.x"].Current).Should().Be((true, true));
        lines["angular-20.x"].Supported.Should().BeFalse();
        (lines["angular-22.x"].Supported, lines["angular-22.x"].Current).Should().Be((true, true));
        // D4: an ecosystem without a support policy falls back to the explicit list; D16 keeps its current line.
        lines["php-3.x"].Supported.Should().BeFalse();
        (lines["php-4.x"].Supported, lines["php-4.x"].Current).Should().Be((true, true));
    }

    [Fact]
    public void Keep_and_retire_override_the_data()
    {
        var lines = Evaluate(new Dictionary<string, string> { ["dotnet-9.x"] = "keep", ["php-3.x"] = "keep", ["dotnet-8.x"] = "retire" });

        (lines["dotnet-9.x"].Supported, lines["dotnet-9.x"].Reason).Should().Be((true, "`keep` in coverage.yml"));
        lines["php-3.x"].Supported.Should().BeTrue();
        (lines["dotnet-8.x"].Supported, lines["dotnet-8.x"].Reason).Should().Be((false, "`retire` in coverage.yml"));
    }

    [Fact]
    public void Each_line_says_why()
    {
        var lines = Evaluate();

        lines["dotnet-8.x"].Reason.Should().Be("dotnet 8 is in maintenance");
        lines["dotnet-9.x"].Reason.Should().Be("dotnet 9 reached end of life");
        lines["dotnet-10.x"].Reason.Should().Be("the current dotnet line");
        lines["php-3.x"].Reason.Should().Be("php has no lifecycle data; `keep` the line to backport to it");
    }

    [Fact]
    public void Lines_are_ordered_by_ecosystem_then_newest_first()
        => SupportedLines.Evaluate(Pattern, Branches, Releases, new Dictionary<string, string>()).Select(l => l.Line.ToString())
            .Should().Equal(["angular-22.x", "angular-20.x", "dotnet-10.x", "dotnet-9.x", "dotnet-8.x", "php-4.x", "php-3.x"]);

    [Theory]
    [InlineData("release/{ecosystem}-{major}.x", "release/")]
    [InlineData("lines/{major}/{ecosystem}", "lines/")]
    [InlineData("{ecosystem}-v{major}", "")]
    public void The_branch_prefix_is_the_patterns_fixed_start(string pattern, string prefix)
        => SupportedLines.BranchPrefix(ReleaseLinePattern.Parse(pattern)).Should().Be(prefix);
}
