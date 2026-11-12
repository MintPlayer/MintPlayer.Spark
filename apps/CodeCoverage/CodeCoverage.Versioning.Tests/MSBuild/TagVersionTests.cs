using System.Collections;
using CodeCoverage.Versioning.Versions;
using Microsoft.Build.Framework;
using MintPlayer.Versioning.MSBuild;
using static CodeCoverage.Versioning.Tests.Releases.ReleasePlannerTests;

namespace CodeCoverage.Versioning.Tests.MSBuild;

/// <summary>
/// #153 M13 (PRD D9): MintPlayer.Versioning.MSBuild's task, compiled here from the package's own source. Its grammar and
/// ordering must agree with the engine's <see cref="PackageVersion"/>, or a build would pack a different version from
/// the one the engine tagged.
/// </summary>
public class TagVersionTests
{
    private static readonly string[] Versions =
    [
        "0.0.0", "0.0.1", "0.1.0", "1.0.0", "1.0.1", "1.2.0", "1.10.0", "2.0.0", "10.0.0", "999999999.0.0",
        "1.0.0-alpha.0", "1.0.0-alpha.1", "1.0.0-alpha.10", "1.0.0-beta.0", "1.0.0-next.3", "1.0.0-preview.1",
        "1.0.0-preview.2", "1.0.0-rc.1", "2.0.0-alpha.0",
        // Outside the grammar: both must refuse them.
        "1.0", "v1.0.0", "01.0.0", "1.0.0-PREVIEW.1", "1.0.0-preview", "1.0.0-preview.01", "1.0.0+meta", "1.0.0-dev.1",
        "1.0.0-preview.1.2", "1000000000.0.0", " 1.0.0", "",
    ];

    [Fact]
    public void Accepts_exactly_the_versions_the_engine_accepts()
    {
        foreach (var version in Versions)
            (TagVersion.Highest([$"A/v{version}"], "A/v") is not null)
                .Should().Be(PackageVersion.TryParse(version, out _), version);
    }

    [Fact]
    public void Orders_versions_as_the_engine_does()
    {
        var valid = Versions.Where(v => PackageVersion.TryParse(v, out _)).ToArray();
        foreach (var left in valid)
        foreach (var right in valid)
            Math.Sign(TagVersion.Compare(left, right))
                .Should().Be(Math.Sign(PackageVersion.Parse(left).CompareTo(PackageVersion.Parse(right))), $"{left} vs {right}");
    }

    [Fact]
    public void Takes_the_highest_tag_of_its_own_package_only()
    {
        var tags = new[] { "Lib/v1.2.0", "Lib/v1.10.0", "Lib/v1.11.0-rc.1", "Lib.Core/v9.0.0", "Lib/v2.0", "v3.0.0", "Lib/v1.11.0-beta.4" };

        TagVersion.Highest(tags, "Lib/v").Should().Be("1.11.0-rc.1");
        TagVersion.Highest(["Lib.Core/v9.0.0", "v1.0.0"], "Lib/v").Should().BeNull();
    }

    [Fact]
    public void On_a_line_takes_the_highest_tag_of_the_packages_major_only()
    {
        // #638 P1: Tools' 10.x line reached master's 11.0.1-rc.1 and packed it over the 10.0.0 the engine had just tagged.
        var tags = new[] { "Lib/v10.0.0", "Lib/v10.0.1", "Lib/v11.0.1-rc.1", "Lib/v9.4.0" };

        TagVersion.Highest(tags, "Lib/v", 10).Should().Be("10.0.1");
        TagVersion.Highest(tags, "Lib/v", 8).Should().BeNull();
        TagVersion.Highest(tags, "Lib/v", null).Should().Be("11.0.1-rc.1");
    }

    [Theory]
    [InlineData("Lib=10;Other=21", "Lib", 10)]
    [InlineData(" Other=21 ; Lib = 10 ", "Lib", 10)]
    [InlineData("Lib.Core=10", "Lib", null)]
    [InlineData("Lib=x", "Lib", null)]
    [InlineData("", "Lib", null)]
    [InlineData(null, "Lib", null)]
    public void Reads_a_packages_major_from_the_actions_map(string? map, string id, int? major)
        => TagVersion.MajorOf(map, id).Should().Be(major);

    [Theory]
    [InlineData(null, "release/dotnet-10.x", 10)]
    [InlineData("release/dotnet-10.x", "123/merge", 10)]
    [InlineData("", "gh-readonly-queue/release/dotnet-10.x/pr-12-0123abcd", 10)]
    [InlineData(null, "release/angular-22.x", null)]
    [InlineData(null, "master", null)]
    [InlineData(null, "release/dotnet-010.x", null)]
    [InlineData(null, null, null)]
    public void Without_the_action_a_dotnet_lines_major_comes_from_the_branch(string? baseRef, string? refName, int? major)
        => TagVersion.BranchMajor(baseRef, refName).Should().Be(major);

    [Fact]
    public void Refuses_a_prefix_that_is_not_a_package_id()
    {
        TagVersion.IsValidPrefix("MintPlayer.Mapper/v").Should().BeTrue();
        foreach (var prefix in new[] { "MintPlayer.Mapper", "*/v", "a b/v", "Lib/v\"; rm", "@scope/pkg/v", "/v", "" })
            TagVersion.IsValidPrefix(prefix).Should().BeFalse(prefix);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "push", true)]
    [InlineData("true", "pull_request", true)]
    [InlineData("true", "pull_request_target", true)]
    [InlineData("true", "merge_group", true)]
    [InlineData("TRUE", "pull_request", true)]
    [InlineData("true", "push", false)]
    [InlineData("true", "workflow_dispatch", false)]
    [InlineData("true", "schedule", false)]
    [InlineData("true", "release", false)]
    [InlineData("true", null, false)]
    [InlineData("true", "Pull_Request", false)]
    public void Packs_an_untagged_package_only_where_nothing_is_published(string? gitHubActions, string? eventName, bool packs)
    {
        TagVersion.PacksUntagged(gitHubActions, eventName).Should().Be(packs);
    }

    [Fact]
    public void The_untagged_version_is_never_read_as_a_tag()
    {
        TagVersion.Highest(["Lib/v" + TagVersion.Untagged], "Lib/v").Should().BeNull();
        PackageVersion.TryParse(TagVersion.Untagged, out _).Should().BeFalse();
    }

    [Fact]
    public void The_task_reads_the_highest_tag_reachable_from_HEAD()
    {
        var work = Directory.CreateTempSubdirectory("versioning-msbuild-");
        try
        {
            var repo = work.FullName;
            Run(repo, "init", "-q", "-b", "main");
            Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "one");
            var engine = new Engine();
            var task = new ResolveTagVersion
            {
                BuildEngine = engine, TagPrefix = "Lib/v", WorkingDirectory = repo, GitHubActions = "true", GitHubEventName = "push",
            };

            task.Execute().Should().BeTrue();
            task.Version.Should().Be(TagVersion.Placeholder);
            engine.Warnings.Should().ContainSingle(w => w == "MPV001");

            // The onboarding pull request: no tag yet, and its build still packs.
            task.GitHubEventName = "pull_request";
            task.Execute().Should().BeTrue();
            task.Version.Should().Be(TagVersion.Untagged);
            engine.Warnings.Should().HaveCount(2).And.OnlyContain(w => w == "MPV001");

            Run(repo, "tag", "-a", "-m", "seed", "Lib/v1.4.0");
            Run(repo, "tag", "-a", "-m", "release", "Lib/v1.5.0-preview.2");
            Run(repo, "switch", "-q", "-c", "side");
            Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "side");
            Run(repo, "tag", "-a", "-m", "elsewhere", "Lib/v2.0.0");
            Run(repo, "switch", "-q", "main");

            task.Execute().Should().BeTrue();
            task.Version.Should().Be("1.5.0-preview.2");

            // #638 D2/D10: the action's map names Lib's major on the line; a tag of another major is never taken, and
            // a line with no tag of its major warns MPV006 (an error where the repository opts in).
            task.Majors = "Lib=1";
            task.Execute().Should().BeTrue();
            task.Version.Should().Be("1.5.0-preview.2");
            task.Majors = "Lib=3";
            task.Execute().Should().BeTrue();
            task.Version.Should().Be(TagVersion.Untagged);
            engine.Warnings.Should().HaveCount(3).And.EndWith("MPV006");

            // Without the action, a dotnet line's branch gives the major, preferred while any tag of it is reachable.
            task.Majors = null;
            Run(repo, "tag", "-a", "-m", "newer", "Lib/v3.0.0");
            task.GitHubRefName = "release/dotnet-1.x";
            task.Execute().Should().BeTrue();
            task.Version.Should().Be("1.5.0-preview.2");
            task.GitHubRefName = "release/dotnet-2.x";
            task.Execute().Should().BeTrue();
            task.Version.Should().Be("3.0.0");
            engine.Warnings.Should().HaveCount(3);
        }
        finally
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            work.Delete(recursive: true);
        }
    }

    private sealed class Engine : IBuildEngine
    {
        public List<string> Warnings { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test.csproj";
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) => throw new InvalidOperationException(e.Message);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e.Code);
    }
}
