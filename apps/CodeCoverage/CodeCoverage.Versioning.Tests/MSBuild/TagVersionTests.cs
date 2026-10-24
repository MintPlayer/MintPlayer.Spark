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
    public void Refuses_a_prefix_that_is_not_a_package_id()
    {
        TagVersion.IsValidPrefix("MintPlayer.Mapper/v").Should().BeTrue();
        foreach (var prefix in new[] { "MintPlayer.Mapper", "*/v", "a b/v", "Lib/v\"; rm", "@scope/pkg/v", "/v", "" })
            TagVersion.IsValidPrefix(prefix).Should().BeFalse(prefix);
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
            var task = new ResolveTagVersion { BuildEngine = engine, TagPrefix = "Lib/v", WorkingDirectory = repo };

            task.Execute().Should().BeTrue();
            task.Version.Should().Be(TagVersion.Placeholder);
            engine.Warnings.Should().ContainSingle(w => w == "MPV001");

            Run(repo, "tag", "-a", "-m", "seed", "Lib/v1.4.0");
            Run(repo, "tag", "-a", "-m", "release", "Lib/v1.5.0-preview.2");
            Run(repo, "switch", "-q", "-c", "side");
            Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "side");
            Run(repo, "tag", "-a", "-m", "elsewhere", "Lib/v2.0.0");
            Run(repo, "switch", "-q", "main");

            task.Execute().Should().BeTrue();
            task.Version.Should().Be("1.5.0-preview.2");
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
