using System.Diagnostics;
using CodeCoverage.Versioning.Changelog;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>#153 M8 (D15): the notes of a Release, grouped from the commit subjects since the package's previous tag.</summary>
public class ReleaseNotesTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("versioning-notes-");

    private static PlannedRelease Planned(string version, string? since = "since", IReadOnlyList<VersionTag>? follows = null, params string[] reasons)
        => new(new ProjectNode { Path = "src/Core/Core.csproj", Kind = PackageKind.NuGet, Id = "Acme.Core", Publishable = true },
            PackageVersion.Parse(version), reasons, null, false, since, ["src/Core"], follows);

    private static async Task<IReadOnlyList<ChangelogEntry>> Entries(params string[] subjects)
        => await ReleaseNotes.EntriesAsync([.. subjects.Select(s => new ChangeCommit(s, ""))], null);

    [Fact]
    public async Task Sections_by_type_drop_the_chores_and_list_the_followed_dependencies()
    {
        // #693 D6: Breaking / Features / Fixes / Performance / Reverts / Changes; docs, ci, chore, … are not written.
        var notes = ReleaseNotes.Compose(Planned("1.3.0", follows: [new VersionTag("Acme.Util", PackageVersion.Parse("2.0.0"))]),
            await Entries("fix(core): null id (#12) (#14)", "feat: lockstep groups (#10) (#11)", "docs: guide (#9)", "feat!: drop net8 (#7)",
                "fix(core): null id (#12) (#14)", "perf: cache (#6)", "untyped change (#5)", "chore(deps): bump x to 2 (#4)", "ci: faster (#3)"));

        notes.Name.Should().Be("Acme.Core 1.3.0");
        notes.Prerelease.Should().BeFalse();
        notes.DependencyOnly.Should().BeFalse();
        notes.Body.Should().Be(
            "### Breaking changes\n- drop net8 (#7)\n\n"
            + "### Features\n- lockstep groups (#11, closes #10)\n\n"
            + "### Fixes\n- null id (#14, closes #12)\n\n"
            + "### Performance\n- cache (#6)\n\n"
            + "### Changes\n- untyped change (#5)\n\n"
            + "### Dependencies\n- bump x to 2 (#4)\n- Dependency update: Acme.Util 2.0.0");
    }

    // #693 M9 (D6): the API delta's removals are breaking, its additions a Public API section, and both make it the package's own.
    [Fact]
    public async Task The_public_api_delta_adds_breaking_lines_and_a_public_api_section()
    {
        var api = new ApiDelta { Added = ["class Acme.New", "type T = `a${string}`"], Removed = ["class Acme.Old"], Omitted = 2 };
        var notes = ReleaseNotes.Compose(Planned("1.3.0", follows: [new VersionTag("Acme.Util", PackageVersion.Parse("2.0.0"))]) with { Api = api },
            await Entries("feat: lists (#4)", "docs: typo (#3)"));

        notes.DependencyOnly.Should().BeFalse();
        notes.Body.Should().Be(
            "### Breaking changes\n- Removed from the public API: `class Acme.Old`\n\n"
            + "### Features\n- lists (#4)\n\n"
            + "### Public API\n- Added `class Acme.New`\n- Added `` type T = `a${string}` ``\n- … and 2 more API changes\n\n"
            + "### Dependencies\n- Dependency update: Acme.Util 2.0.0");
    }

    // #693 M11: versioning.changelog.include writes a type the notes drop by default under "Changes".
    [Fact]
    public async Task Included_types_are_written_under_changes()
    {
        var config = new VersioningConfig { Changelog = new VersioningChangelogConfig { Include = ["docs"] } };
        config.Validate().Should().BeEmpty();

        var notes = ReleaseNotes.Compose(Planned("1.3.0"), await Entries("docs: the guide (#9)", "ci: faster (#3)", "fix: x (#2)"), config.ToSettings().ChangelogInclude);

        notes.Body.Should().Be("### Fixes\n- x (#2)\n\n### Changes\n- the guide (#9)");
        new VersioningConfig { Changelog = new VersioningChangelogConfig { Include = ["feat", "docs"] } }.Validate()
            .Should().Equal("versioning.changelog.include: 'feat' is not one of build, chore, ci, docs, refactor, style, test.");
    }

    [Fact]
    public void An_api_delta_groups_target_frameworks_and_keeps_removals_first()
    {
        ApiDelta.Between(["net8.0 A", "net10.0 A"], ["net8.0 A", "net10.0 A"], prefixedWithTargetFramework: true).Should().BeNull();

        var delta = ApiDelta.Between(
            ["net8.0 A", "net8.0 Gone", "net10.0 A", "net10.0 Gone", "net10.0 OnlyTen"],
            ["net10.0 A", "net10.0 New", "net11.0 A"], prefixedWithTargetFramework: true)!;
        delta.Removed.Should().Equal("target framework net8.0", "Gone", "OnlyTen");
        delta.Added.Should().Equal("target framework net11.0", "New");

        var partial = ApiDelta.Between(["net8.0 A", "net10.0 A"], ["net8.0 A"], prefixedWithTargetFramework: true);
        partial!.Removed.Should().Equal("target framework net10.0");

        var some = ApiDelta.Between(["net8.0 A", "net10.0 A"], ["net8.0 A", "net10.0 A", "net10.0 B"], prefixedWithTargetFramework: true);
        some!.Added.Should().Equal("B (net10.0)");

        var big = ApiDelta.Between([.. Enumerable.Range(0, 80).Select(i => $"old{i:D3}")], [.. Enumerable.Range(0, 80).Select(i => $"new{i:D3}")], false)!;
        (big.Removed.Count, big.Added.Count, big.Omitted).Should().Be((80, 20, 60));
        ApiDelta.Between([], [new string('x', 500)], false)!.Added.Single().Length.Should().Be(ApiDelta.MaxLineLength);
    }

    [Fact]
    public async Task A_release_of_only_dropped_types_and_dependencies_is_dependency_only()
    {
        var notes = ReleaseNotes.Compose(Planned("1.3.1", follows: [new VersionTag("Acme.Util", PackageVersion.Parse("2.0.1"))]),
            await Entries("docs: typo (#9)", "chore(deps): bump y (#8)"));

        notes.DependencyOnly.Should().BeTrue();
        notes.Body.Should().Be("bump y (#8)\nDependency update: Acme.Util 2.0.1");
    }

    [Fact]
    public void A_dependent_only_release_says_dependency_update_and_a_first_release_says_so()
    {
        var dependent = ReleaseNotes.Compose(Planned("1.2.1-preview.1", follows: [new VersionTag("Acme.Util", PackageVersion.Parse("2.0.1"))]), []);
        (dependent.Body, dependent.Prerelease, dependent.DependencyOnly).Should().Be(("Dependency update: Acme.Util 2.0.1", true, true));

        ReleaseNotes.Compose(Planned("1.0.0", since: null, reasons: "first release"), []).Body.Should().Be("First release.");
        ReleaseNotes.Compose(Planned("1.0.1", reasons: "lockstep group core releases"), []).Body.Should().Be("- lockstep group core releases");
    }

    [Fact]
    public async Task Reads_first_parent_subjects_below_the_inputs_and_a_merge_commits_PR_title()
    {
        Write("src/Core/A.cs", "a");
        Write("docs/x.md", "x");
        Git("init", "-q", "-b", "main");
        Git("add", ".");
        Git("commit", "-q", "-m", "chore: initial");
        var since = Git("rev-parse", "HEAD");

        Write("src/Core/A.cs", "b");
        Git("commit", "-q", "-am", "fix: squash merged (#3)");
        Write("docs/x.md", "y");
        Git("commit", "-q", "-am", "docs: elsewhere (#4)");
        Git("checkout", "-q", "-b", "feature");
        Write("src/Core/B.cs", "c");
        Git("add", ".");
        Git("commit", "-q", "-m", "wip on the branch");
        Git("checkout", "-q", "main");
        Git("merge", "-q", "--no-ff", "feature", "-m", "Merge pull request #5 from acme/feature\n\nfeat: merge-committed (#2)");
        var head = Git("rev-parse", "HEAD");

        var subjects = await ReleaseNotes.SubjectsAsync(new GitCli(Path.Combine(root.FullName, ".git")), since, head, ["src/Core"]);

        subjects.Should().Equal("feat: merge-committed (#2) (#5)", "fix: squash merged (#3)");
    }

    [Fact]
    public async Task A_promote_merge_on_a_release_line_lists_the_pull_requests_it_brings_in()
    {
        // #693 D4: /release makes a commit whose tree is a master commit and whose parents are the line tip and that
        // commit. The first-parent walk saw only "Promote …"; MintPlayer.Spark/v11.1.0 missed #690 that way.
        Write("src/Core/A.cs", "a");
        Git("init", "-q", "-b", "master");
        Git("add", ".");
        Git("commit", "-q", "-m", "chore: initial");
        Git("branch", "release/1.x");
        var since = Git("rev-parse", "HEAD");

        Write("src/Core/A.cs", "b");
        Git("commit", "-q", "-am", "feat: templates (#690)");
        Write("docs/x.md", "x");
        Git("add", ".");
        Git("commit", "-q", "-m", "docs: guide (#691)");
        Write("src/Core/A.cs", "c");
        Git("commit", "-q", "-am", "fix: null id (#692)");
        var master = Git("rev-parse", "HEAD");

        Git("checkout", "-q", "release/1.x");
        var tree = Git("rev-parse", master + "^{tree}");
        var promote = Git("commit-tree", tree, "-p", "HEAD", "-p", master, "-m", $"Promote release/1.x to master {master[..12]}");
        Git("reset", "-q", "--hard", promote);

        var subjects = await ReleaseNotes.SubjectsAsync(new GitCli(Path.Combine(root.FullName, ".git")), since, promote, ["src/Core"]);

        subjects.Should().Equal("fix: null id (#692)", "feat: templates (#690)");
    }

    [Fact]
    public async Task A_second_promotion_lists_only_what_master_gained_since_the_first_one_on_the_package_s_paths()
    {
        // Changelog × release lines R4 (G4): since is the first promotion's merge, whose second parent (master then) and its
        // ancestors are excluded; a commit outside the package's paths is not listed.
        Write("src/Core/A.cs", "a");
        Git("init", "-q", "-b", "master");
        Git("add", ".");
        Git("commit", "-q", "-m", "chore: initial");
        Git("branch", "release/1.x");

        Write("src/Core/A.cs", "b");
        Git("commit", "-q", "-am", "feat: first (#1)");
        var first = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "release/1.x");
        var promoted = Git("commit-tree", Git("rev-parse", first + "^{tree}"), "-p", "HEAD", "-p", first, "-m", "Promote master into release/1.x");
        Git("reset", "-q", "--hard", promoted);

        Git("checkout", "-q", "master");
        Write("src/Core/A.cs", "c");
        Git("commit", "-q", "-am", "fix: second (#2)");
        Write("docs/x.md", "x");
        Git("add", ".");
        Git("commit", "-q", "-m", "docs: outside the package (#3)");
        var master = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "release/1.x");
        var again = Git("commit-tree", Git("rev-parse", master + "^{tree}"), "-p", "HEAD", "-p", master, "-m", "Promote master into release/1.x");

        var git = new GitCli(Path.Combine(root.FullName, ".git"));
        (await ReleaseNotes.SubjectsAsync(git, promoted, again, ["src/Core"])).Should().Equal("fix: second (#2)");
        // Planned as if promoted (R1): from master's head, the same list.
        (await ReleaseNotes.SubjectsAsync(git, promoted, master, ["src/Core"])).Should().Equal("fix: second (#2)");
    }

    [Fact]
    public async Task A_merge_queue_backport_with_a_trailer_and_co_authors_and_its_original_are_one_entry()
    {
        // R4 (G6): the merge queue's squash commit carries the Backport-of trailer among Co-authored-by lines.
        Write("src/Core/A.cs", "a");
        Git("init", "-q", "-b", "master");
        Git("add", ".");
        Git("commit", "-q", "-m", "chore: initial");
        Git("branch", "release/1.x");
        var since = Git("rev-parse", "HEAD");

        Write("src/Core/A.cs", "b");
        Git("commit", "-q", "-am", "fix: null id (#609)");
        var master = Git("rev-parse", "HEAD");

        Git("checkout", "-q", "release/1.x");
        Git("cherry-pick", "-n", master);
        Git("commit", "-q", "-m", "[1.x] fix: null id (#610)\n\n* fix: null id\n\nBackport-of: #609\nCo-authored-by: Ann <ann@example.invalid>\nCo-authored-by: Bob <bob@example.invalid>");
        var promote = Git("commit-tree", Git("rev-parse", master + "^{tree}"), "-p", "HEAD", "-p", master, "-m", "Promote master into release/1.x");

        var notes = ReleaseNotes.Compose(Planned("1.1.0"),
            await ReleaseNotes.EntriesAsync(await ReleaseNotes.CommitsAsync(new GitCli(Path.Combine(root.FullName, ".git")), since, promote, ["src/Core"]), null));

        notes.Body.Should().Be("### Fixes\n- null id (#609)");
    }

    [Fact]
    public async Task A_backport_and_its_original_arriving_through_a_promotion_are_one_entry()
    {
        // #693 D5: the line had the change as a backport (a Backport-of trailer, or git's cherry-pick -x line); the next
        // promotion brings master's original too. The key is the original pull request, so each change is listed once.
        Write("src/Core/A.cs", "a");
        Write("src/Core/B.cs", "a");
        Git("init", "-q", "-b", "master");
        Git("add", ".");
        Git("commit", "-q", "-m", "chore: initial");
        Git("branch", "release/1.x");
        var since = Git("rev-parse", "HEAD");

        Write("src/Core/A.cs", "b");
        Git("commit", "-q", "-am", "fix: null id (#251) (#609)");
        var original = Git("rev-parse", "HEAD");
        Write("src/Core/B.cs", "b");
        Git("commit", "-q", "-am", "feat: picker (#612)");
        var picked = Git("rev-parse", "HEAD");
        var master = picked;

        Git("checkout", "-q", "release/1.x");
        Git("cherry-pick", "-n", original);
        Git("commit", "-q", "-m", "[1.x] fix: null id (#251) (#610)\n\nBackport-of: #609");
        Git("cherry-pick", "-x", picked);
        Git("commit", "-q", "--amend", "-m", $"[1.x] feat: picker (#613)\n\n(cherry picked from commit {picked})");
        var tree = Git("rev-parse", master + "^{tree}");
        var promote = Git("commit-tree", tree, "-p", "HEAD", "-p", master, "-m", $"Promote release/1.x to master {master[..12]}");

        var notes = ReleaseNotes.Compose(Planned("1.1.0"),
            await ReleaseNotes.EntriesAsync(await ReleaseNotes.CommitsAsync(new GitCli(Path.Combine(root.FullName, ".git")), since, promote, ["src/Core"]), null));

        notes.Body.Should().Be("### Features\n- picker (#612)\n\n### Fixes\n- null id (#609, closes #251)");
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", "user.name=t", "-c", "user.email=t@t" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, string.Join(' ', args));
        return output.Trim();
    }

    public void Dispose()
    {
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        root.Delete(recursive: true);
    }
}
