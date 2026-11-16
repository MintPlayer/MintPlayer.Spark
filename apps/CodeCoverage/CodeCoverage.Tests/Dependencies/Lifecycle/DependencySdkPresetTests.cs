using CodeCoverage.Dependencies.Lifecycle;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>#658: the bot pre-sets global.json only for a repository whose pinned dependency action declares <c>sdk-preset</c>.</summary>
public class DependencySdkPresetTests
{
    [Fact]
    public void The_pinned_ref_is_read_from_the_apply_workflow()
    {
        var template = DependencyOnboarding.ReadTemplate("dependency-apply.yml");
        // A repository not re-onboarded yet still pins a commit (D-L14, superseded 2026-11-13).
        var pinned = template.Replace(DependencyOnboarding.ActionReference, $"{DependencyOnboarding.ActionPath}@{new string('a', 40)} # {DependencyOnboarding.ActionTag}", StringComparison.Ordinal);

        DependencySdkPreset.PinnedRef(template).Should().Be(DependencyOnboarding.ActionTag);
        DependencySdkPreset.PinnedRef(pinned).Should().Be(new string('a', 40));
        DependencySdkPreset.PinnedRef("jobs:\n  a:\n    steps:\n      - uses: actions/checkout@v7\n").Should().BeNull();
        DependencySdkPreset.PinnedRef(null).Should().BeNull();
    }

    [Fact]
    public void Two_different_pins_in_one_workflow_name_no_ref()
    {
        const string workflow = """
            steps:
              - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/dependency-action@aaaa
              - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/dependency-action@bbbb
            """;

        DependencySdkPreset.PinnedRef(workflow).Should().BeNull();
    }

    [Fact]
    public void Only_a_capabilities_line_naming_sdk_preset_declares_it()
    {
        DependencySdkPreset.Declares("# capabilities: sdk-preset\nname: x\n").Should().BeTrue();
        DependencySdkPreset.Declares("# capabilities: other, sdk-preset\nname: x\n").Should().BeTrue();
        DependencySdkPreset.Declares("# capabilities: sdk-preset-v2\nname: x\n").Should().BeFalse();
        DependencySdkPreset.Declares("name: x\ndescription: 'sdk-preset'\n").Should().BeFalse();
        DependencySdkPreset.Declares(null).Should().BeFalse();
    }

    /// <summary>The action in this repository declares it: the commit the tag moves to after #658 is one the bot pre-sets for.</summary>
    [Fact]
    public void This_repository_s_action_declares_sdk_preset()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, DependencySdkPreset.ActionYmlPath)))
            root = Path.GetDirectoryName(root);

        root.Should().NotBeNull();
        DependencySdkPreset.Declares(File.ReadAllText(Path.Combine(root!, DependencySdkPreset.ActionYmlPath))).Should().BeTrue();
    }
}
