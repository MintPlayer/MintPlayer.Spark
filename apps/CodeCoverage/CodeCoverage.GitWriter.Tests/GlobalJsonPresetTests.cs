using System.Text;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #658 (docs/setup_migrations_PRD.md): the plan commit pre-sets the root <c>global.json</c> to the SDK the .NET platform
/// group moves to, so <c>dependency-apply.yml</c>'s <c>setup-dotnet</c> installs it. SpaServices #111: the group moved
/// <c>11.0.100-rc.2…</c> to <c>11.0.100</c> on a runner that had only the preview.
/// </summary>
public class GlobalJsonPresetTests : IDisposable
{
    private const string Preview = """
        {
          "sdk": {
            "version": "11.0.100-rc.2.26504.105",
            "rollForward": "latestFeature"
          }
        }

        """;

    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static string Plan(params PlanPlatform?[] platforms) => DependencyContract.Serialize(new DependencyPlan
    {
        PlanId = "p1",
        ExpectedSha = new string('a', 40),
        Groups = [.. platforms.Select((p, i) => new PlanGroup { Group = $"g{i}", Platform = p })],
    });

    private static PlanPlatform Dotnet(string from = "11.0", string to = "11.0", string sdk = "11.0.100") => new()
    {
        Sdk = "dotnet", FromChannel = from, ToChannel = to, ToVersion = $"{to}.0", SdkVersion = sdk,
    };

    [Fact]
    public void An_in_channel_move_sets_only_the_version_value()
    {
        var preset = GlobalJsonPreset.Preset(Preview, Plan(null, Dotnet()));

        preset.Should().Be(Preview.Replace("11.0.100-rc.2.26504.105", "11.0.100"));
    }

    [Fact]
    public void A_cross_channel_move_keeps_crlf_and_the_rest_of_the_file()
    {
        var text = "{\r\n  \"sdk\": { \"version\": \"10.0.100\", \"allowPrerelease\": false },\r\n  \"msbuild-sdks\": {}\r\n}\r\n";

        GlobalJsonPreset.Preset(text, Plan(Dotnet("10.0", "11.0", "11.0.100")))
            .Should().Be(text.Replace("10.0.100", "11.0.100"));
    }

    [Fact]
    public void Pins_the_platform_group_would_not_move_are_left_alone()
    {
        var other = Preview.Replace("11.0.100-rc.2.26504.105", "9.0.300");
        var newer = Preview.Replace("11.0.100-rc.2.26504.105", "11.0.101");

        GlobalJsonPreset.Preset(other, Plan(Dotnet())).Should().BeNull("a pin on another channel is not the group's");
        GlobalJsonPreset.Preset(newer, Plan(Dotnet())).Should().BeNull("an in-channel move never goes back");
        GlobalJsonPreset.Preset(Preview.Replace("11.0.100-rc.2.26504.105", "11.0.100"), Plan(Dotnet())).Should().BeNull("already there");
        GlobalJsonPreset.Preset("{ \"msbuild-sdks\": {} }\n", Plan(Dotnet())).Should().BeNull("no sdk.version");
    }

    [Fact]
    public void Plans_without_exactly_one_unscoped_dotnet_platform_group_set_nothing()
    {
        GlobalJsonPreset.Preset(Preview, Plan()).Should().BeNull();
        GlobalJsonPreset.Preset(Preview, Plan(new PlanPlatform { Sdk = "node", FromChannel = "11.0", ToChannel = "11.0", SdkVersion = "11.0.100" })).Should().BeNull();
        GlobalJsonPreset.Preset(Preview, Plan(Dotnet() with { SdkVersion = null })).Should().BeNull();
        GlobalJsonPreset.Preset(Preview, Plan(Dotnet() with { Locations = ["src/**"] })).Should().BeNull();
        GlobalJsonPreset.Preset(Preview, Plan(Dotnet(), Dotnet())).Should().BeNull();
        GlobalJsonPreset.Preset(Preview, "{}").Should().BeNull("a plan the contract cannot read");
    }

    [Fact]
    public async Task The_plan_commit_rule_accepts_the_plan_file_plus_exactly_the_preset()
    {
        var expected = repo.Commit(("global.json", Preview));
        var plan = Plan(Dotnet());
        var commit = Child(expected, (DependencyFiles.PlanPath, plan), ("global.json", GlobalJsonPreset.Preset(Preview, plan)!));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, commit)).Should().BeNull();
    }

    [Fact]
    public async Task The_plan_commit_rule_refuses_any_other_global_json_change()
    {
        var expected = repo.Commit(("global.json", Preview));
        var plan = Plan(Dotnet());
        var otherVersion = Child(expected, (DependencyFiles.PlanPath, plan), ("global.json", Preview.Replace("11.0.100-rc.2.26504.105", "11.0.101")));
        var otherKey = Child(expected, (DependencyFiles.PlanPath, plan), ("global.json", Preview.Replace("11.0.100-rc.2.26504.105", "11.0.100").Replace("latestFeature", "major")));
        var noPlatform = Plan();
        var withoutGroup = Child(expected, (DependencyFiles.PlanPath, noPlatform), ("global.json", GlobalJsonPreset.Preset(Preview, plan)!));
        var nested = Child(expected, (DependencyFiles.PlanPath, plan), ("src/global.json", GlobalJsonPreset.Preset(Preview, plan)!));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, otherVersion)).Should().NotBeNullOrWhiteSpace();
        (await PlanCommitRule.CheckAsync(repo.Git, expected, otherKey)).Should().NotBeNullOrWhiteSpace();
        (await PlanCommitRule.CheckAsync(repo.Git, expected, withoutGroup)).Should().NotBeNullOrWhiteSpace();
        (await PlanCommitRule.CheckAsync(repo.Git, expected, nested)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_plan_commit_rule_refuses_a_global_json_the_planned_commit_did_not_have()
    {
        var expected = repo.Commit(("package.json", "{}\n"));
        var plan = Plan(Dotnet());
        var added = Child(expected, (DependencyFiles.PlanPath, plan), ("global.json", Preview));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, added)).Should().NotBeNullOrWhiteSpace();
    }

    private string Child(string parent, params (string Path, string Content)[] files)
    {
        var entries = new List<(string, string, byte[])>();
        foreach (var line in repo.Git(["ls-tree", "-r", parent]).GetAwaiter().GetResult().StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var meta = line[..tab].Split(' ');
            var path = line[(tab + 1)..];
            if (files.Any(f => f.Path == path)) continue;
            entries.Add((path, meta[0], Encoding.UTF8.GetBytes(repo.Git(["cat-file", "blob", meta[2]]).GetAwaiter().GetResult().StandardOutput)));
        }
        foreach (var (path, content) in files)
            entries.Add((path, "100644", Encoding.UTF8.GetBytes(content)));
        return repo.Commit(entries, parent);
    }
}
