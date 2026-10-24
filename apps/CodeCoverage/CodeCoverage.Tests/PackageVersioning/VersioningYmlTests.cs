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
              groups:
                - name: spark
                  packages: ['MintPlayer.Spark*']
              packages:
                MintPlayer.Spark:
                  anchor: free
            """);

        parsed.Errors.Should().BeEmpty();
        var config = parsed.Config!;
        config.EffectiveMode.Should().Be("local");
        config.Dispatch.Should().Be("publish.yml");
        config.EffectiveReleaseBranches.Should().BeEquivalentTo(["release/*.x", "lts/*"]);
        config.Groups!.Single().Packages.Should().BeEquivalentTo(["MintPlayer.Spark*"]);
        config.Packages!["MintPlayer.Spark"]!.Anchor.Should().Be("free");
    }

    [Theory]
    [InlineData("versioning:\n  mode: sometimes\n")]
    [InlineData("versioning:\n  dispatch: ../evil.sh\n")]
    [InlineData("versioning:\n  releaseBranches: ['release/../main']\n")]
    [InlineData("versioning:\n  packages:\n    Acme:\n      anchor: 'not an id'\n")]
    [InlineData("versioning: [unclosed\n")]
    public void An_unusable_section_reports_why(string yml)
    {
        var parsed = VersioningYml.Parse(yml);

        parsed.Config.Should().NotBeNull();
        parsed.Errors.Should().NotBeEmpty();
    }
}
