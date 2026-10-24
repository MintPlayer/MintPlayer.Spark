using System.Diagnostics;
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

    [Fact]
    public void Groups_the_subjects_by_type_and_lists_the_followed_dependencies()
    {
        var notes = ReleaseNotes.Compose(Planned("1.3.0", follows: [new VersionTag("Acme.Util", PackageVersion.Parse("2.0.0"))]),
            ["fix(core): null id (#12) (#14)", "feat: lockstep groups (#10) (#11)", "docs: guide (#9)", "feat!: drop net8 (#7)", "fix(core): null id (#12) (#14)"]);

        notes.Name.Should().Be("Acme.Core 1.3.0");
        notes.Prerelease.Should().BeFalse();
        notes.Body.Should().Be(
            "### Features\n- feat: lockstep groups (#10) (#11)\n- feat!: drop net8 (#7)\n\n"
            + "### Fixes\n- fix(core): null id (#12) (#14)\n\n"
            + "### Other changes\n- docs: guide (#9)\n\n"
            + "### Dependencies\n- Dependency update: Acme.Util 2.0.0");
    }

    [Fact]
    public void A_dependent_only_release_says_dependency_update_and_a_first_release_says_so()
    {
        var dependent = ReleaseNotes.Compose(Planned("1.2.1-preview.1", follows: [new VersionTag("Acme.Util", PackageVersion.Parse("2.0.1"))]), []);
        (dependent.Body, dependent.Prerelease).Should().Be(("Dependency update: Acme.Util 2.0.1", true));

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
