using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Onboarding;
using CodeCoverage.PackageVersioning;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// Owner, 2026-11-13: the workflows call the bot's actions at their moving tag, never at a commit. A pinned commit only moved
/// when the rolling pull request that bumped it merged, so a fix to the action could not reach the run that needed it
/// (Dotnet.Tools #208: run 43483039627 ran master's pin 5c99269 while #208 carried 7c6e7f33, and every NuGet group failed
/// with NETSDK1045). Re-onboarding moves an existing pin back to the tag.
/// </summary>
public class ActionTagReferenceTests
{
    private const string Sha = "5c992693d01ba9e70eb355df3f2c4979057b4108";
    private const string Dependency = "MintPlayer/MintPlayer.Spark/apps/CodeCoverage/dependency-action";

    [Fact]
    public void A_commit_pin_with_the_tag_as_a_comment_moves_to_the_tag()
    {
        var text = $"    steps:\n      - name: Apply\n        uses: {Dependency}@{Sha} # dependency-update-v1\n        with:\n          mode: apply\n";

        ActionTagReference.AtTag(text, Dependency, DependencyOnboarding.ActionTag)
            .Should().Be($"    steps:\n      - name: Apply\n        uses: {Dependency}@dependency-update-v1\n        with:\n          mode: apply\n");
    }

    [Theory]
    [InlineData("uses: {0}@{1}\n", "uses: {0}@dependency-update-v1\n")]
    [InlineData("uses: {0}@{1}   #   dependency-update-v1  \r\n", "uses: {0}@dependency-update-v1\r\n")]
    [InlineData("- uses: {0}@{1} # dependency-update-v1\n", "- uses: {0}@dependency-update-v1\n")]
    public void A_pin_without_a_comment_with_spacing_or_CRLF_or_as_a_list_item_moves_too(string input, string expected)
        => ActionTagReference.AtTag(string.Format(input, Dependency, Sha), Dependency, DependencyOnboarding.ActionTag)
            .Should().Be(string.Format(expected, Dependency));

    [Theory]
    [InlineData("uses: {0}@dependency-update-v1\n")]
    [InlineData("uses: {0}@{1} # dependency-update-v2\n")]
    [InlineData("uses: {0}@abc1234 # dependency-update-v1\n")]
    [InlineData("uses: actions/checkout@{1} # v5\n")]
    [InlineData("uses: {0}-fork@{1} # dependency-update-v1\n")]
    public void The_tag_another_tag_a_short_sha_another_action_or_a_longer_path_stay_as_they_are(string input)
    {
        var text = string.Format(input, Dependency, Sha);
        ActionTagReference.AtTag(text, Dependency, DependencyOnboarding.ActionTag).Should().Be(text);
    }

    [Fact]
    public void The_versioning_action_moves_to_its_own_tag()
        => ActionTagReference.AtTag($"        uses: {VersioningOnboarding.ActionPath}@{Sha} # versioning-v1\n", VersioningOnboarding.ActionPath, VersioningOnboarding.ActionTag)
            .Should().Be($"        uses: {VersioningOnboarding.ActionPath}@versioning-v1\n");
}
