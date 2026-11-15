using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// companion-version-maps-PRD M1 (§6.1): a <c>platforms.&lt;sdk&gt;.companions</c> entry is a glob (channel rule),
/// <c>builtin</c>, or <c>{ match, versions | bundled }</c>.
/// </summary>
public class CompanionEntriesYmlTests
{
    private static IReadOnlyList<DependencyCompanionEntry> Companions(string entries, string sdk = "dotnet")
    {
        var parsed = DependenciesYml.Parse($"dependencies:\n  platforms:\n    {sdk}:\n      companions:\n{entries}");
        parsed.Errors.Should().BeEmpty();
        return parsed.Section.Platforms[sdk].Companions!;
    }

    [Fact]
    public void Every_entry_form_parses()
    {
        var entries = Companions("""
                    - "MyCompany.Platform.*"
                    - match: "Microsoft.CodeAnalysis*"
                      versions: { "10.0": "<=5.0", "11.0.1xx": "5.12.x" }
                    - match: "Microsoft.Build*"
                      bundled: msbuild

            """);

        entries.Should().HaveCount(3);
        entries[0].Should().Be(new DependencyCompanionEntry("MyCompany.Platform.*"));
        entries[1].Match.Should().Be("Microsoft.CodeAnalysis*");
        entries[1].Versions!["10.0"].Should().Be("<=5.0");
        entries[1].Versions!["11.0.1xx"].Should().Be("5.12.x");
        entries[1].Bundled.HasValue.Should().BeFalse();
        entries[2].Bundled.Should().Be(EDependencyBundledComponent.Msbuild);
        entries[2].Versions.Should().BeNull();
    }

    [Fact]
    public void Builtin_expands_to_the_built_in_entries_at_its_place()
    {
        var entries = Companions("""
                    - match: "Microsoft.Build*"
                      versions: { "11.0": "<=18.6" }
                    - builtin

            """);

        entries[0].Versions!["11.0"].Should().Be("<=18.6", "the repository's own entry comes first, so it wins");
        entries.Skip(1).Should().Equal(DependencyCompanions.BuiltIn("dotnet"));
    }

    [Fact]
    public void A_plain_glob_list_no_longer_implies_the_built_ins()
        => Companions("        - \"Microsoft.AspNetCore.*\"\n").Should().Equal(new DependencyCompanionEntry("Microsoft.AspNetCore.*"));

    [Fact]
    public void The_built_ins_tie_msbuild_roslyn_and_nuget_to_the_sdk_band()
    {
        var builtIn = DependencyCompanions.BuiltIn("dotnet");

        builtIn.Should().Contain(new DependencyCompanionEntry("Microsoft.Build*", Bundled: EDependencyBundledComponent.Msbuild));
        builtIn.Should().Contain(new DependencyCompanionEntry("Microsoft.CodeAnalysis*", Bundled: EDependencyBundledComponent.Roslyn));
        builtIn.Should().Contain(new DependencyCompanionEntry("Microsoft.Net.Compilers.Toolset", Bundled: EDependencyBundledComponent.Roslyn));
        builtIn.Should().Contain(new DependencyCompanionEntry("NuGet.*", Bundled: EDependencyBundledComponent.Nuget));
        builtIn.Should().Contain(new DependencyCompanionEntry("dotnet-ef"));
        DependencyCompanions.BuiltIn("node").Should().Equal(new DependencyCompanionEntry("@types/node"));
    }

    /// <summary>Globs match case-insensitively and the first match wins, so <c>Microsoft.NET.*</c> must not come first.</summary>
    [Fact]
    public void The_compiler_toolset_is_a_roslyn_companion_not_a_channel_rule()
        => new DependencyPolicyConfig().CompanionEntry("dotnet", "Microsoft.Net.Compilers.Toolset")!.Bundled.Should().Be(EDependencyBundledComponent.Roslyn);

    [Theory]
    [InlineData("dotnet", "{ match: x, versions: { \"11\": \"5.x\" } }", "'11' is not a channel")]
    [InlineData("dotnet", "{ match: x, versions: { \"11.0.1x\": \"5.x\" } }", "'11.0.1x' is not a channel")]
    [InlineData("dotnet", "{ match: x, versions: { \"11.0\": \"banana\" } }", "'banana' is not a version range")]
    [InlineData("dotnet", "{ match: x, versions: {} }", "versions is empty")]
    [InlineData("dotnet", "{ match: x, bundled: compiler }", "bundled 'compiler'")]
    [InlineData("dotnet", "{ match: x, bundled: msbuild, versions: { \"11.0\": \"1.x\" } }", "either versions or bundled")]
    [InlineData("dotnet", "{ versions: { \"11.0\": \"1.x\" } }", "match")]
    [InlineData("dotnet", "{ match: x, colour: red }", "'colour'")]
    [InlineData("node", "{ match: x, bundled: msbuild }", "bundled is for dotnet")]
    [InlineData("node", "{ match: x, versions: { \"22.1xx\": \"1.x\" } }", "band")]
    public void A_malformed_entry_is_an_error(string sdk, string entry, string mentioned)
    {
        var parsed = DependenciesYml.Parse($"dependencies:\n  platforms:\n    {sdk}:\n      companions: [{entry}]\n");

        parsed.Errors.Should().Contain(e => e.Contains($"dependencies.platforms.{sdk}.companions[0]") && e.Contains(mentioned));
    }
}
