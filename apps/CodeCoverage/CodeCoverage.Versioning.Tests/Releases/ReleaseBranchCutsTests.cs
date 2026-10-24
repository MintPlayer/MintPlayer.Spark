using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>
/// M9: which release branches a default-branch run asks for (D12). A platform package that leaves its major cuts the old
/// line at the first parent; a free package, or a run on a release line, never does.
/// </summary>
public class ReleaseBranchCutsTests
{
    private const string Hash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private sealed class FakeDiff(params string[] changed) : IInputDiff
    {
        public Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(changed.Where(f => paths.Any(p => RepoPath.IsUnder(f, p))).ToList());
    }

    private static TaggedVersion Tag(string id, string version)
        => new(VersionTag.For(id, PackageVersion.Parse(version)), "c1", new TagAnnotation(Hash, null));

    private static string Library(string frameworks, string references = "")
        => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{(frameworks.Contains(';') ? "TargetFrameworks" : "TargetFramework")}>{frameworks}</{(frameworks.Contains(';') ? "TargetFrameworks" : "TargetFramework")}></PropertyGroup><ItemGroup>{references}</ItemGroup></Project>";

    private static InMemoryTree Tree(string coreFrameworks) => new InMemoryTree()
        .Add("src/Core/Core.csproj", Library(coreFrameworks))
        .Add("src/Core/Guard.cs", "")
        .Add("src/Mapper/Mapper.csproj", Library("net10.0", "<ProjectReference Include=\"../Core/Core.csproj\" />"))
        .Add("src/Text/Text.csproj", Library("netstandard2.0"))
        .Add("src/Text/Text.cs", "");

    private static async Task<ReleasePlan> PlanAsync(InMemoryTree tree, TaggedVersion[] tags, bool onDefaultBranch = true, int? branchMajor = null, params string[] changed)
    {
        var graph = await ProjectGraphBuilder.BuildAsync(tree);
        return await ReleasePlanner.PlanAsync(new ReleaseContext
        {
            Graph = graph,
            Head = "head",
            Tags = new TagHistory(tags),
            Diff = new FakeDiff(changed),
            Build = graph.Packages.ToDictionary(p => p.Id!, _ => new PackageBuildFacts { ApiHash = Hash }),
            OnDefaultBranch = onDefaultBranch,
            BranchMajor = branchMajor,
        });
    }

    private static readonly TaggedVersion[] Tags = [Tag("Core", "10.1.0"), Tag("Mapper", "10.1.0"), Tag("Text", "3.0.0")];

    [Fact]
    public async Task A_package_that_moves_to_the_next_platform_leaves_its_old_major()
    {
        var plan = await PlanAsync(Tree("net11.0"), Tags, changed: ["src/Core/Guard.cs", "src/Text/Text.cs"]);

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0");
        plan.Of("Core")!.MovedFromMajor.Should().Be(10);
        // Mapper stays on net10.0: a patch on its own line, no move.
        plan.Of("Mapper")!.MovedFromMajor.HasValue.Should().BeFalse();
        // A free package never cuts (D12).
        plan.Of("Text")!.MovedFromMajor.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_multi_target_package_moves_with_its_highest_platform()
    {
        var plan = await PlanAsync(Tree("net10.0;net11.0"), Tags, changed: "src/Core/Guard.cs");

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0");
        plan.Of("Core")!.MovedFromMajor.Should().Be(10);
    }

    [Fact]
    public async Task A_release_line_beside_a_newer_major_moves_nothing()
    {
        // release/10.x patches 10.x while 11.x lives on the default branch: the two lines side by side.
        var plan = await PlanAsync(Tree("net10.0"), [.. Tags, Tag("Core", "11.0.0")], onDefaultBranch: false, branchMajor: 10, "src/Core/Guard.cs");

        plan.Of("Core")!.Version.ToString().Should().Be("10.1.1");
        plan.Of("Core")!.MovedFromMajor.HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "release/*.x" }, 8, "release/8.x")]
    [InlineData(new[] { "main-only", "v*/*", "support/*" }, 10, "support/10")]
    [InlineData(new[] { "v*/*" }, 10, null)]
    public void The_branch_is_named_by_the_first_pattern_with_a_single_star(string[] patterns, int major, string? branch)
        => ReleaseBranchCuts.NameFor(patterns, major).Should().Be(branch);

    [Theory]
    [InlineData("Merge pull request #42 from acme/net11", 42)]
    [InlineData("feat: target .NET 11 (#42)", 42)]
    [InlineData("feat: target .NET 11 (#42) for real", null)]
    [InlineData("feat: target .NET 11", null)]
    public void The_pull_request_comes_from_the_merge_or_squash_subject(string subject, int? number)
        => ReleaseBranchCuts.PullRequestOf(subject).Should().Be(number);

    [Fact]
    public async Task The_cut_is_at_the_first_parent_and_names_the_pull_request()
    {
        using var repository = new Repository();
        var first = repository.Commit("first");
        var head = repository.Commit("feat: target .NET 11 (#42)");
        var plan = await PlanAsync(Tree("net11.0"), Tags, changed: "src/Core/Guard.cs");
        var warnings = new List<string>();

        var cuts = await ReleaseBranchCuts.ForAsync(repository.Git, head, plan.Releases, ReleaseBranches.Default, warnings);

        cuts.Select(c => (c.Branch, c.Major, c.Commit, c.PullRequest)).Should().Equal(("release/10.x", 10, first, (int?)42));
        cuts[0].Packages.Should().Equal("Core");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_root_commit_cuts_nothing_and_says_so()
    {
        using var repository = new Repository();
        var head = repository.Commit("first");
        var plan = await PlanAsync(Tree("net11.0"), Tags, changed: "src/Core/Guard.cs");
        var warnings = new List<string>();

        var cuts = await ReleaseBranchCuts.ForAsync(repository.Git, head, plan.Releases, ReleaseBranches.Default, warnings);

        cuts.Should().BeEmpty();
        warnings.Should().ContainSingle(w => w.Contains("no first parent"));
    }

    private sealed class Repository : IDisposable
    {
        private readonly DirectoryInfo work = Directory.CreateTempSubdirectory("versioning-cuts-");

        public Repository() => Run("init", "-q", "-b", "main");

        public GitCli Git => new(Path.Combine(work.FullName, ".git"));

        public string Commit(string message)
        {
            File.WriteAllText(Path.Combine(work.FullName, Guid.NewGuid().ToString("N") + ".txt"), message);
            Run("add", ".");
            Run("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
            return Git.RunAsync(["rev-parse", "HEAD"]).GetAwaiter().GetResult().EnsureSuccess("rev-parse").Text.Trim();
        }

        private void Run(params string[] args) => ReleasePlannerTests.Run(work.FullName, args);

        public void Dispose()
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            work.Delete(recursive: true);
        }
    }
}
