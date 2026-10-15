using System.Text;
using CodeCoverage.Git;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The T4 gate on the regenerate patch, over patches written by real <c>git diff --binary</c> between trees built with
/// the git CLI (memory: parsers need REAL fixtures), plus hand-made patches for shapes git never writes but an attacker
/// would.
/// </summary>
public class PatchValidatorTests : IDisposable
{
    private const string Hashes = "apps/DemoApp/DemoApp/App_Data/modelHashes.json";
    private const string Model = "apps/DemoApp/DemoApp/App_Data/Model/Order.json";
    private static readonly IReadOnlyList<PathGlob> Regenerated = [new("**/App_Data/modelHashes.json"), new("**/App_Data/Model/*.json")];

    private readonly TempDir dir = new("trees");
    private readonly string repo;
    private int indexes;

    public PatchValidatorTests()
    {
        repo = Path.Combine(dir.Path, "trees.git");
        FixtureGit.Run(null, "init", "--quiet", "--bare", repo);
    }

    public void Dispose() => dir.Dispose();

    /// <summary>A tree holding exactly these entries; a 160000 entry's content is the commit id it points at.</summary>
    private string Tree(params (string Path, string Mode, string Content)[] entries)
    {
        var index = Path.Combine(dir.Path, $"{++indexes}.index");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        foreach (var (path, mode, content) in entries)
        {
            string oid;
            if (mode == "160000")
                oid = content;
            else
            {
                var file = Path.Combine(dir.Path, $"blob-{Guid.NewGuid():N}");
                File.WriteAllBytes(file, Encoding.UTF8.GetBytes(content));
                oid = FixtureGit.Run(repo, "hash-object", "-w", file).Trim();
            }
            FixtureGit.RunWith(repo, env, "update-index", "--add", "--cacheinfo", $"{mode},{oid},{path}");
        }
        return FixtureGit.RunWith(repo, env, "write-tree").Trim();
    }

    private string Diff(string from, string to, params string[] options) => FixtureGit.Run(repo, ["diff", "--binary", .. options, from, to]);

    private static PatchVerdict Validate(string patch, IReadOnlyList<PathGlob>? globs = null, long max = 1024 * 1024)
        => PatchValidator.Validate(Encoding.UTF8.GetBytes(patch), globs ?? Regenerated, max);

    [Fact]
    public void Edits_additions_deletions_and_renames_inside_the_regenerated_paths_are_accepted()
    {
        var before = Tree((Hashes, "100644", "{\n  \"modelHash\": \"a\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/Old.json", "100644", "{\n  \"name\": \"Old\"\n}\n"));
        var after = Tree((Hashes, "100644", "{\n  \"modelHash\": \"b\"\n}\n"), (Model, "100644", "{\n  \"name\": \"Order\"\n}\n"));
        var renamed = Tree((Hashes, "100644", "{\n  \"modelHash\": \"a\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/New.json", "100644", "{\n  \"name\": \"Old\"\n}\n"));

        var edit = Validate(Diff(before, after, "--no-renames"));
        var rename = Validate(Diff(before, renamed, "-M"));

        edit.Accepted.Should().BeTrue(edit.Detail ?? "");
        edit.Paths.Should().BeEquivalentTo([Hashes, Model, "apps/DemoApp/DemoApp/App_Data/Model/Old.json"]);
        rename.Accepted.Should().BeTrue(rename.Detail ?? "");
        rename.Paths.Should().BeEquivalentTo(["apps/DemoApp/DemoApp/App_Data/Model/Old.json", "apps/DemoApp/DemoApp/App_Data/Model/New.json"]);
    }

    [Fact]
    public void A_path_outside_the_regenerated_paths_is_rejected_even_as_a_rename_target()
    {
        var before = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"));
        var source = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"), ("src/Program.cs", "100644", "evil();\n"));
        var moved = Tree(("src/Order.json", "100644", "{\n  \"name\": \"Order\"\n}\n"));

        var edit = Validate(Diff(before, source));
        var rename = Validate(Diff(before, moved, "-M"));

        edit.Reason.Should().Be(PatchReasons.OutsideRegeneratedPaths);
        edit.Paths.Should().Equal(["src/Program.cs"]);
        rename.Reason.Should().Be(PatchReasons.OutsideRegeneratedPaths);
        rename.Paths.Should().Equal(["src/Order.json"]);
    }

    /// <summary>O37: the app sends the concrete file a default keep-both rule resolved as an exact entry; it allows that
    /// file and no sibling, where the default's glob <c>**/*.json</c> would have allowed every JSON file.</summary>
    [Fact]
    public void A_concrete_resolved_path_allows_that_file_and_not_its_siblings()
    {
        IReadOnlyList<PathGlob> globs = [new("**/App_Data/modelHashes.json"), new("App/security.json")];
        var before = Tree(("App/security.json", "100644", "{\n  \"a\": 1\n}\n"), ("App/appsettings.json", "100644", "{\n  \"b\": 1\n}\n"));
        var resolved = Tree(("App/security.json", "100644", "{\n  \"a\": 2\n}\n"), ("App/appsettings.json", "100644", "{\n  \"b\": 1\n}\n"));
        var sibling = Tree(("App/security.json", "100644", "{\n  \"a\": 1\n}\n"), ("App/appsettings.json", "100644", "{\n  \"b\": 2\n}\n"));

        var allowed = Validate(Diff(before, resolved), globs);
        var refused = Validate(Diff(before, sibling), globs);

        allowed.Accepted.Should().BeTrue(allowed.Detail ?? "");
        allowed.Paths.Should().Equal(["App/security.json"]);
        refused.Reason.Should().Be(PatchReasons.OutsideRegeneratedPaths);
        refused.Paths.Should().Equal(["App/appsettings.json"]);
        new PathGlob("App/security.json").Matches("App/security.jsonx").Should().BeFalse("a path without a wildcard matches only itself");
        new PathGlob("App/security.json").Matches("Other/App/security.json").Should().BeFalse();
    }

    [Fact]
    public void Symlinks_submodules_executables_mode_changes_binaries_and_copies_are_rejected()
    {
        var before = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"));
        var link = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/Link.json", "120000", "../../../../../etc/passwd"));
        var submodule = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/Sub.json", "160000", "0123456789abcdef0123456789abcdef01234567"));
        var executable = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/Run.json", "100755", "{}\n"));
        var modeChange = Tree((Model, "100755", "{\n  \"name\": \"Order\"\n}\n"));
        var binary = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/Bin.json", "100644", "\0\u0001\u0002binary"));
        var copied = Tree((Model, "100644", "{\n  \"name\": \"Order\"\n}\n"), ("apps/DemoApp/DemoApp/App_Data/Model/Copy.json", "100644", "{\n  \"name\": \"Order\"\n}\n"));

        Validate(Diff(before, link)).Reason.Should().Be(PatchReasons.Symlink);
        Validate(Diff(before, submodule)).Reason.Should().Be(PatchReasons.Submodule);
        Validate(Diff(before, executable)).Reason.Should().Be(PatchReasons.UnsupportedMode);
        Validate(Diff(before, modeChange)).Reason.Should().Be(PatchReasons.ModeChange);
        var bin = Diff(before, binary);
        bin.Should().Contain("GIT binary patch", "control: git wrote a binary patch");
        Validate(bin).Reason.Should().Be(PatchReasons.Binary);
        var copy = Diff(before, copied, "-C", "--find-copies-harder");
        copy.Should().Contain("copy from", "control: git wrote a copy");
        Validate(copy).Reason.Should().Be(PatchReasons.Copy);
    }

    [Theory]
    [InlineData(".github/workflows/regenerate.yml", "**")]
    [InlineData(".GITHUB/workflows/x.yml", "**")]
    [InlineData("apps/.gitmodules", "**")]
    [InlineData("apps/DemoApp/.gitattributes", "**")]
    [InlineData("apps/.git/config", "**")]
    [InlineData("apps/../x.json", "**")]
    public void Paths_that_could_change_how_git_or_ci_behaves_are_unsafe_even_when_a_glob_allows_them(string path, string glob)
    {
        var patch = $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-a\n+b\n";

        Validate(patch, [new PathGlob(glob)]).Reason.Should().Be(PatchReasons.UnsafePath);
    }

    [Fact]
    public void A_short_hunk_cannot_smuggle_a_second_file_header_past_the_counts()
    {
        // git apply counts hunk lines; after the one-line hunk it would read "--- a/src/Evil.cs" as a NEW traditional
        // diff. The validator counts the same way, so the extra lines are outside any hunk and refused.
        var patch = $"diff --git a/{Model} b/{Model}\n--- a/{Model}\n+++ b/{Model}\n@@ -1 +1 @@\n-a\n+b\n--- a/src/Evil.cs\n+++ b/src/Evil.cs\n@@ -1 +1 @@\n-x\n+y\n";

        var verdict = Validate(patch);

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be(PatchReasons.Malformed);
    }

    [Theory]
    [InlineData("From 0123 Mon Sep 17 00:00:00 2001\n")]
    [InlineData("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n")]
    public void Text_outside_a_diff_git_section_is_refused(string prefix)
    {
        var patch = prefix + $"diff --git a/{Model} b/{Model}\n--- a/{Model}\n+++ b/{Model}\n@@ -1 +1 @@\n-a\n+b\n";

        Validate(patch).Reason.Should().Be(PatchReasons.Malformed);
    }

    [Fact]
    public void Quoted_paths_nul_bytes_invalid_utf8_and_oversized_patches_are_refused()
    {
        var quoted = "diff --git \"a/apps/App_Data/Model/\\303\\251.json\" \"b/apps/App_Data/Model/\\303\\251.json\"\n--- \"a/apps/App_Data/Model/\\303\\251.json\"\n+++ \"b/apps/App_Data/Model/\\303\\251.json\"\n@@ -1 +1 @@\n-a\n+b\n";
        var good = $"diff --git a/{Model} b/{Model}\n--- a/{Model}\n+++ b/{Model}\n@@ -1 +1 @@\n-a\n+b\n";

        Validate(quoted).Accepted.Should().BeFalse();
        PatchValidator.Validate([.. Encoding.UTF8.GetBytes(good), 0], Regenerated, 1024).Reason.Should().Be(PatchReasons.Binary);
        PatchValidator.Validate([.. Encoding.UTF8.GetBytes(good), 0xC3], Regenerated, 1024).Reason.Should().Be(PatchReasons.Malformed);
        Validate(good, max: 10).Reason.Should().Be(PatchReasons.TooLarge);
        Validate(good).Accepted.Should().BeTrue();
    }
}
