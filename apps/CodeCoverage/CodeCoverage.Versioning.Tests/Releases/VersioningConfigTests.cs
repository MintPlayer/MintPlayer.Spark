using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>What coverage.yml's versioning section refuses, each with the key it names.</summary>
public class VersioningConfigTests
{
    private static VersioningGroupConfig Group(string? name, params string[]? packages) => new() { Name = name, Packages = packages?.ToList() };

    [Theory]
    [InlineData("")]
    [InlineData("src/a**b")]
    public void An_invalid_ignore_glob_is_refused(string glob)
        => new VersioningConfig { Ignore = [glob] }.Validate().Should().ContainSingle().Which.Should().StartWith("versioning.ignore: '");

    public static TheoryData<VersioningGroupConfig?, string> RefusedGroups => new()
    {
        { null, "versioning.groups[0].name is missing" },
        { Group("", "Acme.*"), "versioning.groups[0].name is missing" },
        { Group("a\u0001b", "Acme.*"), "versioning.groups[0].name is missing" },
        { Group("core", null), "versioning.groups[0].packages must list 1 to" },
        { Group("core"), "versioning.groups[0].packages must list 1 to" },
        { Group("core", "Acme Core"), "versioning.groups[0].packages: 'Acme Core' is not a package id pattern." },
    };

    [Theory]
    [MemberData(nameof(RefusedGroups))]
    public void A_group_needs_a_name_and_package_patterns(VersioningGroupConfig? group, string error)
        => new VersioningConfig { Groups = [group!] }.Validate().Should().Contain(e => e.StartsWith(error, StringComparison.Ordinal));

    [Fact]
    public void A_group_name_is_used_once()
        => new VersioningConfig { Groups = [Group("core", "Acme.*"), Group("core", "Other.*")] }.Validate()
            .Should().Equal("versioning.groups[1]: the name 'core' is used twice.");

    [Fact]
    public void A_package_key_must_be_a_package_id()
        => new VersioningConfig { Packages = new() { ["not an id"] = new VersioningPackageConfig() } }.Validate()
            .Should().Equal("versioning.packages: 'not an id' is not a package id.");

    [Fact]
    public void Accepted_groups_reach_the_settings_as_lockstep_groups()
    {
        var config = new VersioningConfig { Groups = [Group("core", "Acme.*", "Other.Core")] };
        config.Validate().Should().BeEmpty();

        var group = config.ToSettings().Groups.Should().ContainSingle().Which;

        (group.Name, string.Join(",", group.Packages)).Should().Be(("core", "Acme.*,Other.Core"));
    }
}
