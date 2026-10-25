using System.Diagnostics;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>M4: the release set (D4), lockstep groups (D13) and hand-pushed tags (D3).</summary>
public class ReleasePlannerTests
{
    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class FakeDiff(params string[] changed) : IInputDiff
    {
        public Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(changed.Where(f => paths.Any(p => RepoPath.IsUnder(f, p))).ToList());
    }

    private static TaggedVersion Tag(string id, string version, string commit = "c1")
        => new(VersionTag.For(id, PackageVersion.Parse(version)), commit, new TagAnnotation(Hash, null));

    private static string Library(string references = "", string tfm = "net10.0")
        => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup><ItemGroup>{references}</ItemGroup></Project>";

    private static string Reference(string path) => $"<ProjectReference Include=\"{path}\" />";

    private static async Task<ReleasePlan> Plan(InMemoryTree tree, IEnumerable<TaggedVersion> tags, IInputDiff diff, VersioningSettings? settings = null, string head = "head")
    {
        var graph = await ProjectGraphBuilder.BuildAsync(tree);
        var build = graph.Packages.ToDictionary(p => p.Id!, _ => new PackageBuildFacts { ApiHash = Hash });
        return await ReleasePlanner.PlanAsync(new ReleaseContext
        {
            Graph = graph,
            Head = head,
            Tags = new TagHistory(tags),
            Diff = diff,
            Settings = settings ?? new VersioningSettings(),
            Build = build,
        });
    }

    private static InMemoryTree NpmWorkspace(string dependentSection, string range) => new InMemoryTree()
        .Add("package.json", """{ "name": "root", "private": true, "workspaces": ["packages/*"] }""")
        .Add("packages/util/package.json", """{ "name": "@acme/util", "version": "0.0.0-PLACEHOLDER" }""")
        .Add("packages/util/index.js", "")
        .Add("packages/app/package.json", $$"""{ "name": "@acme/app", "version": "0.0.0-PLACEHOLDER", "{{dependentSection}}": { "@acme/util": "{{range}}" } }""");

    [Fact]
    public async Task Npm_example_1_a_caret_range_admits_the_patch_so_the_dependent_stays()
    {
        var plan = await Plan(NpmWorkspace("dependencies", "^1.2.0"), [Tag("@acme/util", "1.2.0"), Tag("@acme/app", "1.0.0")], new FakeDiff("packages/util/index.js"));

        plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["@acme/util/v1.2.1"]);
    }

    [Fact]
    public async Task Npm_example_2_a_tilde_range_that_drops_the_new_minor_releases_the_dependent()
    {
        var tags = new[] { Tag("@acme/util", "1.2.0"), Tag("@acme/app", "1.0.0") };
        var graph = await ProjectGraphBuilder.BuildAsync(NpmWorkspace("dependencies", "~1.2.0"));
        var plan = await ReleasePlanner.PlanAsync(new ReleaseContext
        {
            Graph = graph,
            Head = "head",
            Tags = new TagHistory(tags),
            Diff = new FakeDiff("packages/util/index.js"),
            Build = new Dictionary<string, PackageBuildFacts>
            {
                ["@acme/util"] = new() { ApiHash = "sha256:" + new string('b', 64) },
                ["@acme/app"] = new() { ApiHash = Hash },
            },
        });

        plan.Of("@acme/util")!.Version.ToString().Should().Be("1.3.0");
        plan.Of("@acme/app")!.Reasons.Should().Contain(r => r.Contains("outside '~1.2.0'"));
    }

    [Fact]
    public async Task Npm_example_3_a_peer_range_that_admits_the_release_needs_nothing()
    {
        var plan = await Plan(NpmWorkspace("peerDependencies", ">=1.0.0 <2.0.0"), [Tag("@acme/util", "1.2.0"), Tag("@acme/app", "1.0.0")], new FakeDiff("packages/util/index.js"));

        plan.Of("@acme/app").Should().BeNull();
    }

    [Fact]
    public async Task An_exact_npm_pin_releases_the_dependent()
    {
        var plan = await Plan(NpmWorkspace("dependencies", "1.2.0"), [Tag("@acme/util", "1.2.0"), Tag("@acme/app", "1.0.0")], new FakeDiff("packages/util/index.js"));

        plan.Of("@acme/app")!.Version.ToString().Should().Be("1.0.1");
    }

    private static InMemoryTree DotnetChain() => new InMemoryTree()
        .Add("src/Core/Core.csproj", Library())
        .Add("src/Core/Guard.cs", "")
        .Add("src/Core/README.md", "")
        .Add("src/Mapper/Mapper.csproj", Library(Reference("../Core/Core.csproj")))
        .Add("src/Mapper.Json/Mapper.Json.csproj", Library(Reference("../Mapper/Mapper.csproj")))
        .Add("src/Cli/Cli.csproj", Library());

    private static TaggedVersion[] DotnetTags() =>
        [Tag("Core", "10.1.0"), Tag("Mapper", "10.1.0"), Tag("Mapper.Json", "10.0.4"), Tag("Cli", "10.0.0")];

    [Fact]
    public async Task A_dotnet_release_cascades_to_every_dependent()
    {
        var plan = await Plan(DotnetChain(), DotnetTags(), new FakeDiff("src/Core/Guard.cs"));

        plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["Core/v10.1.1", "Mapper/v10.1.1", "Mapper.Json/v10.0.5"]);
        plan.Of("Mapper.Json")!.Reasons.Should().Contain("Mapper 10.1.1 is released");
    }

    [Fact]
    public async Task Ignored_files_release_nothing()
    {
        var plan = await Plan(DotnetChain(), DotnetTags(), new FakeDiff("src/Core/README.md"));

        plan.Releases.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_project_changes_release_nothing()
    {
        var tree = DotnetChain()
            .Add("src/Core/Tests/Core.Tests.csproj", Library("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" />"))
            .Add("src/Core/Tests/GuardTests.cs", "");

        var plan = await Plan(tree, DotnetTags(), new FakeDiff("src/Core/Tests/GuardTests.cs"));

        plan.Releases.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lockstep_group_releases_together_at_its_highest_version()
    {
        var settings = new VersioningSettings { Groups = [new LockstepGroup("mapper", ["Mapper", "Mapper.*"])] };
        var plan = await Plan(DotnetChain(), DotnetTags(), new FakeDiff("src/Mapper/Mapper.csproj"), settings);

        plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["Mapper/v10.1.1", "Mapper.Json/v10.1.1"]);
    }

    [Fact]
    public async Task A_lockstep_group_with_two_majors_is_refused()
    {
        var tree = DotnetChain().Add("src/Cli/Cli.csproj", Library(tfm: "net11.0"));
        var settings = new VersioningSettings { Groups = [new LockstepGroup("all", ["*"])] };

        var plan = await Plan(tree, DotnetTags(), new FakeDiff("src/Core/Guard.cs"), settings);

        plan.Releases.Should().BeEmpty();
        plan.Refused.Select(n => n.Id).Should().Contain("Cli");
    }

    [Fact]
    public async Task A_human_tag_on_head_is_a_fixed_version_and_dependents_follow()
    {
        var plan = await Plan(DotnetChain(), [.. DotnetTags(), Tag("Core", "10.5.0", "head")], new FakeDiff());

        plan.Of("Core")!.TaggedByHand.Should().BeTrue();
        plan.Of("Core")!.Version.ToString().Should().Be("10.5.0");
        plan.Of("Mapper").Should().NotBeNull();
    }

    private static TaggedVersion EngineTag(string id, string version, string commit)
        => new(VersionTag.For(id, PackageVersion.Parse(version)), commit, new TagAnnotation(Hash, "1.2.0"));

    [Fact]
    public async Task A_rerun_of_a_tagged_commit_releases_the_same_versions_as_the_engines_own()
    {
        // #544 D3: the first run tagged Core and its dependents on head, then failed before the packages shipped.
        TaggedVersion[] tags = [.. DotnetTags(), EngineTag("Core", "10.1.1", "head"), EngineTag("Mapper", "10.1.1", "head"), EngineTag("Mapper.Json", "10.0.5", "head")];

        var plan = await Plan(DotnetChain(), tags, new FakeDiff("src/Core/Guard.cs"));

        plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["Core/v10.1.1", "Mapper/v10.1.1", "Mapper.Json/v10.0.5"]);
        plan.Releases.Should().AllSatisfy(r => r.TaggedByHand.Should().BeFalse());
        plan.Of("Core")!.Since.Should().Be("c1");
        plan.Of("Core")!.Reasons.Should().ContainSingle(r => r.Contains("released by an earlier run"));
    }

    [Fact]
    public async Task A_later_commit_packs_an_unchanged_package_at_its_dangling_tag()
    {
        // The run that tagged Core 10.1.1 on c2 never published it; head changes only Cli, so Core is not released again
        // and the pack reads Core/v10.1.1, the highest reachable tag.
        var plan = await Plan(DotnetChain(), [.. DotnetTags(), EngineTag("Core", "10.1.1", "c2")], new FakeDiff("src/Cli/Cli.csproj"));

        plan.Of("Core").Should().BeNull();
        plan.Of("Cli")!.Version.ToString().Should().Be("10.0.1");
    }

    [Fact]
    public async Task A_package_without_tags_is_a_first_release()
    {
        var plan = await Plan(DotnetChain(), [], new FakeDiff());

        plan.Of("Cli")!.Version.ToString().Should().Be("10.0.0");
        plan.Of("Cli")!.Reasons.Should().Contain("first release");
    }

    [Fact]
    public async Task A_package_cycle_fails_the_run()
    {
        var tree = new InMemoryTree()
            .Add("A/A.csproj", Library(Reference("../B/B.csproj")))
            .Add("B/B.csproj", Library(Reference("../A/A.csproj")));

        await ((Func<Task>)(() => Plan(tree, [], new FakeDiff()))).Should().ThrowAsync<GraphException>();
    }

    [Fact]
    public async Task Git_tags_and_tree_diffs_are_read_from_the_repository()
    {
        var work = Directory.CreateTempSubdirectory("versioning-release-");
        try
        {
            var root = work.FullName;
            Directory.CreateDirectory(Path.Combine(root, "Core"));
            await File.WriteAllTextAsync(Path.Combine(root, "Core", "Core.csproj"), Library());
            Run(root, "init", "-q");
            Run(root, "add", ".");
            Commit(root, "init");
            var message = Path.Combine(root, ".git", "TAGMSG");
            await File.WriteAllTextAsync(message, new TagAnnotation(Hash, Engine.Version).Format(VersionTag.For("Core", PackageVersion.Parse("10.0.0"))));
            Run(root, "tag", "-a", "Core/v10.0.0", "-F", message);
            Run(root, "tag", "not-a-version");
            await File.WriteAllTextAsync(Path.Combine(root, "Core", "Guard.cs"), "class Guard {}");
            Run(root, "add", ".");
            Commit(root, "change");

            var git = new GitCli(Path.Combine(root, ".git"));
            var head = (await git.RunAsync(["rev-parse", "HEAD"])).EnsureSuccess("rev-parse").Text.Trim();
            var tags = await TagHistory.ReadAsync(git);
            var core = tags.Of("Core").Should().ContainSingle().Which;
            core.Annotation!.ApiHash.Should().Be(Hash);

            (await new GitInputDiff(git).ChangedAsync(core.Commit, head, ["Core"])).Should().BeEquivalentTo(["Core/Guard.cs"]);

            var plan = await ReleasePlanner.PlanAsync(new ReleaseContext
            {
                Graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, head)),
                Head = head,
                Tags = tags,
                Diff = new GitInputDiff(git),
                Build = new Dictionary<string, PackageBuildFacts> { ["Core"] = new() { ApiHash = Hash } },
            });
            plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["Core/v10.0.1"]);
        }
        finally
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            work.Delete(recursive: true);
        }

        static void Commit(string root, string message) => Run(root, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
    }

    internal static void Run(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_COMMITTER_EMAIL"] = "t@t";
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
    }
}
