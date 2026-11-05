using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #552 D1–D3: a plan-only run decides the tags and writes none; the release half writes exactly those tags later, and
/// refuses when the tag history moved in between. Runs the local host's composition on an origin's work checkout.
/// </summary>
public class VersioningPlanReleaseTests : WriterTestBase
{
    private const string CoreHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string UtilHash = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string Run = "https://github.com/acme/widgets/actions/runs/42";

    private static Origin Fixture(Origin origin)
    {
        origin.Write("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n");
        origin.Write("package.json", "{ \"private\": true, \"workspaces\": [\"packages/*\"] }\n");
        origin.Write("packages/util/package.json", "{ \"name\": \"@acme/util\", \"version\": \"0.0.0\" }\n");
        origin.Commit("packages");
        return origin.Push("main");
    }

    private static ReleaseRunRequest Request(string commit) => new(new ReleaseManifest
    {
        Commit = commit,
        Packages =
        [
            new ManifestPackage { Id = "Core", Kind = "nuget", ApiHash = CoreHash, TargetFrameworks = ["net10.0"], Packed = true },
            new ManifestPackage { Id = "@acme/util", Kind = "npm", ApiHash = UtilHash, Packed = true },
        ],
    }, new VersioningConfig().ToSettings(), OnDefaultBranch: true, BranchMajor: null, Run);

    private static GitCli Git(Origin origin)
    {
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "--tags", "--force", "origin");
        return new GitCli(Path.Combine(origin.Work, ".git"), TimeSpan.FromMinutes(5));
    }

    private static TagWriter TagWriterFor(GitCli git)
        => new(new CliTagGit(git, token: null), "origin", ["refs/remotes/origin/main"], new Tagger("github-actions[bot]", "bot@example.invalid"));

    private static string Tags(Origin origin) => origin.InBare("for-each-ref", "refs/tags", "--format=%(refname) %(*objectname)%0a%(contents)");

    [Fact]
    public async Task A_plan_writes_no_tag_and_its_release_writes_the_tags_a_run_writes()
    {
        using var planned = new Origin(Server, "planned");
        using var ran = new Origin(Server, "ran");
        Fixture(planned);
        Fixture(ran);

        var (result, plan) = await ReleaseRun.PlanOnlyAsync(Git(planned), Request(planned.Head()));
        result.Succeeded.Should().BeTrue();
        result.Tags.Should().OnlyContain(t => t.Outcome == TagOutcome.Planned);
        result.Notes.Should().NotBeNull().And.HaveCount(2, "notes are computed for planned tags as for written ones");
        plan.Should().NotBeNull();
        plan!.Tags.Select(t => t.Name).Should().BeEquivalentTo(new[] { "Core/v10.0.0", "@acme/util/v1.0.0" });
        Tags(planned).Should().BeEmpty("a plan pushes nothing");

        var git = Git(planned);
        var written = await ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan);
        written.Should().OnlyContain(t => t.Outcome == TagOutcome.Created);

        var runGit = Git(ran);
        (await ReleaseRun.RunAsync(runGit, TagWriterFor(runGit), Request(ran.Head()))).Succeeded.Should().BeTrue();
        Tags(planned).Should().NotBeEmpty().And.Be(Tags(ran), "plan + release tags exactly what one run tags, annotation included");
    }

    [Fact]
    public async Task A_release_that_already_pushed_its_tags_runs_again()
    {
        using var origin = new Origin(Server, "rerun");
        Fixture(origin);
        var (_, plan) = await ReleaseRun.PlanOnlyAsync(Git(origin), Request(origin.Head()));

        var git = Git(origin);
        await ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan!);
        var again = Git(origin);
        var second = await ReleaseRun.ReleaseAsync(again, TagWriterFor(again), plan!);

        second.Should().OnlyContain(t => t.Outcome == TagOutcome.AlreadyPresent, "the plan's own tags are left out of the fingerprint");
    }

    [Fact]
    public async Task A_version_tag_pushed_after_the_plan_makes_it_stale()
    {
        using var origin = new Origin(Server, "stale");
        Fixture(origin);
        var (_, plan) = await ReleaseRun.PlanOnlyAsync(Git(origin), Request(origin.Head()));

        // A hand-pushed major (#552 D3): the plan decided Core 10.0.0 without it.
        FixtureGit.Run(origin.Work, "tag", "Core/v11.0.0");
        FixtureGit.Run(origin.Work, "push", "--quiet", "origin", "refs/tags/Core/v11.0.0");
        FixtureGit.Run(origin.Work, "tag", "--delete", "Core/v11.0.0");

        var git = Git(origin);
        var release = () => ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan!);

        (await release.Should().ThrowAsync<StalePlanException>()).WithMessage("*Re-run all jobs*");
        Tags(origin).Should().NotContain("refs/tags/@acme/util/v1.0.0", "a stale plan writes nothing");
    }

    /// <summary>A run on <c>release/dotnet-10.x</c> with lines on (#568 D1): it plans Core 10.0.0 and leaves the plain node package out.</summary>
    private static ReleaseRunRequest LineRequest(string commit) => Request(commit) with
    {
        Settings = new VersioningConfig { Lines = new VersioningLinesConfig() }.ToSettings(),
        OnDefaultBranch = false,
        Line = new ReleaseLine("dotnet", 10),
    };

    private static void PushTag(Origin origin, string tag)
    {
        FixtureGit.Run(origin.Work, "tag", tag);
        FixtureGit.Run(origin.Work, "push", "--quiet", "origin", "refs/tags/" + tag);
        FixtureGit.Run(origin.Work, "tag", "--delete", tag);
    }

    [Theory]
    [InlineData("Core/v11.0.0")]       // release/dotnet-11.x released in between
    [InlineData("@acme/util/v1.0.0")]  // another ecosystem's line released in between
    public async Task Another_line_releasing_in_between_leaves_a_line_plan_fresh(string otherLine)
    {
        // #568 S9, the red of D17: with #552's whole-history fingerprint this release was refused as stale.
        using var origin = new Origin(Server, "lines-" + otherLine.Replace('/', '-').Replace('@', 'a'));
        Fixture(origin);
        var (result, plan) = await ReleaseRun.PlanOnlyAsync(Git(origin), LineRequest(origin.Head()));
        result.Succeeded.Should().BeTrue();
        plan!.Scoped.Should().BeTrue();
        plan.Tags.Select(t => t.Name).Should().Equal("Core/v10.0.0");

        PushTag(origin, otherLine);
        var git = Git(origin);
        var written = await ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan);

        written.Should().ContainSingle(t => t.Tag.Name == "Core/v10.0.0" && t.Outcome == TagOutcome.Created);
    }

    [Fact]
    public async Task A_line_plan_made_stale_on_its_own_line_is_still_refused()
    {
        using var origin = new Origin(Server, "lines-stale");
        Fixture(origin);
        var (_, plan) = await ReleaseRun.PlanOnlyAsync(Git(origin), LineRequest(origin.Head()));

        // Another release of the same package at the same major: the plan's 10.0.0 was decided without it.
        PushTag(origin, "Core/v10.0.1");
        var git = Git(origin);
        var release = () => ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan!);

        await release.Should().ThrowAsync<StalePlanException>();
        Tags(origin).Should().NotContain("refs/tags/Core/v10.0.0");
    }

    /// <summary>The default branch with lines on (#568 D15): it tags nothing.</summary>
    private static ReleaseRunRequest DefaultBranchWithLines(string commit) => Request(commit) with
    {
        Settings = new VersioningConfig { Lines = new VersioningLinesConfig() }.ToSettings(),
    };

    [Fact]
    public async Task The_default_branch_with_lines_tags_nothing_and_cuts_the_line_of_its_major()
    {
        using var origin = new Origin(Server, "lines-cut");
        Fixture(origin);
        var head = origin.Head();

        var (result, plan) = await ReleaseRun.PlanOnlyAsync(Git(origin), DefaultBranchWithLines(head));

        result.Plan.Releases.Should().BeEmpty();
        plan!.Tags.Should().BeEmpty();
        var cut = result.BranchCuts.Should().ContainSingle().Which;
        cut.Should().BeEquivalentTo(new { Branch = "release/dotnet-10.x", Major = 10, Commit = head, Ecosystem = "dotnet" });
        cut.Packages.Should().Equal("Core");
    }

    [Fact]
    public async Task A_line_that_has_released_is_not_cut_again()
    {
        using var origin = new Origin(Server, "lines-cut-done");
        Fixture(origin);
        PushTag(origin, "Core/v10.0.0");

        var (result, _) = await ReleaseRun.PlanOnlyAsync(Git(origin), DefaultBranchWithLines(origin.Head()));

        result.BranchCuts.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task A_tampered_plan_is_refused_before_anything_is_written()
    {
        using var origin = new Origin(Server, "tampered");
        Fixture(origin);
        var (_, plan) = await ReleaseRun.PlanOnlyAsync(Git(origin), Request(origin.Head()));

        var git = Git(origin);
        var badTag = () => ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan! with { Tags = [new PlannedTag("not a tag", null)] });
        var badHash = () => ReleaseRun.ReleaseAsync(git, TagWriterFor(git), plan! with { Tags = [new PlannedTag("Core/v10.0.0", "md5:abc")] });

        await badTag.Should().ThrowAsync<VersioningException>();
        await badHash.Should().ThrowAsync<VersioningException>();
        Tags(origin).Should().BeEmpty();
    }
}
