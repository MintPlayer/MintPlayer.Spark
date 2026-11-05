using CodeCoverage.PackageVersioning;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>D17: a repository is versioned exactly when its default-branch <c>coverage.yml</c> has a <c>versioning:</c> key.</summary>
public class VersioningYmlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("comment:\n  enabled: true\n")]
    public void Without_the_key_the_repository_is_not_versioned(string? yml)
        => VersioningYml.Parse(yml).Config.Should().BeNull();

    [Fact]
    public void An_empty_section_is_versioned_with_every_default()
    {
        var parsed = VersioningYml.Parse("versioning:\n");

        parsed.Config.Should().NotBeNull();
        parsed.Errors.Should().BeEmpty();
        parsed.Config!.EffectiveMode.Should().Be("server");
        parsed.Config.EffectiveReleaseBranches.Should().BeEquivalentTo(["release/*.x"]);
        parsed.Config.EffectiveLines.Should().BeNull();
    }

    [Theory]
    [InlineData("versioning:\n  lines:\n", "release/{ecosystem}-{major}.x")]
    [InlineData("versioning:\n  lines: {}\n", "release/{ecosystem}-{major}.x")]
    [InlineData("versioning:\n  lines:\n    pattern: support/{ecosystem}/{major}\n", "support/{ecosystem}/{major}")]
    public void A_lines_key_turns_release_lines_on(string yml, string pattern)
    {
        // #568 D15: an empty `lines:` is YAML null, and must switch lines on all the same.
        var parsed = VersioningYml.Parse(yml);

        parsed.Errors.Should().BeEmpty();
        parsed.Config!.EffectiveLines!.Text.Should().Be(pattern);
        parsed.Config.IsVersionedBranch("release/dotnet-10.x", "main", out var major).Should().Be(pattern.StartsWith("release/"));
        parsed.Config.IsVersionedBranch("release/10.x", "main", out _).Should().BeFalse();
    }

    [Fact]
    public void A_full_section_is_read_in_camel_case()
    {
        var parsed = VersioningYml.Parse("""
            versioning:
              mode: local
              dispatch: publish.yml
              releaseBranches: ['release/*.x', 'lts/*']
              ignore: ['docs/**']
              prerelease: preview
              groups:
                - name: spark
                  packages: ['MintPlayer.Spark*']
              packages:
                MintPlayer.Spark:
                  anchor: free
                "@mintplayer/ng-spark": { prerelease: upstream }
            """);

        parsed.Errors.Should().BeEmpty();
        var config = parsed.Config!;
        config.EffectiveMode.Should().Be("local");
        config.Dispatch.Should().Be("publish.yml");
        config.EffectiveReleaseBranches.Should().BeEquivalentTo(["release/*.x", "lts/*"]);
        config.Groups!.Single().Packages.Should().BeEquivalentTo(["MintPlayer.Spark*"]);
        config.Packages!["MintPlayer.Spark"]!.Anchor.Should().Be("free");
        config.Prerelease.Should().Be("preview");
        config.Packages!["@mintplayer/ng-spark"]!.Prerelease.Should().Be("upstream");
    }

    [Theory]
    [InlineData("versioning:\n  mode: sometimes\n")]
    [InlineData("versioning:\n  dispatch: ../evil.sh\n")]
    [InlineData("versioning:\n  releaseBranches: ['release/../main']\n")]
    [InlineData("versioning:\n  packages:\n    Acme:\n      anchor: 'not an id'\n")]
    [InlineData("versioning:\n  prerelease: canary\n")]
    [InlineData("versioning: [unclosed\n")]
    public void An_unusable_section_reports_why(string yml)
    {
        var parsed = VersioningYml.Parse(yml);

        parsed.Config.Should().NotBeNull();
        parsed.Errors.Should().NotBeEmpty();
    }
}
