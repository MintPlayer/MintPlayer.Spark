using Xunit;
using CodeCoverage.Ingestion;

namespace CodeCoverage.Tests.Ingestion;

public class PathNormalizerTests
{
    private static readonly string[] FileList =
    [
        "src/Calculator.cs",
        "src/util.ts",
        "src/main/java/com/acme/App.java",
        "tools/util.ts",
    ];

    [Fact]
    public void Strips_the_workspace_root_from_absolute_ci_paths()
    {
        var normalizer = new PathNormalizer("/home/runner/work/repo/repo", [], FileList);

        var (path, matched) = normalizer.Normalize("/home/runner/work/repo/repo/src/Calculator.cs");

        path.Should().Be("src/Calculator.cs");
        matched.Should().BeTrue();
    }

    [Fact]
    public void Strips_report_declared_source_roots_and_unifies_slashes()
    {
        var normalizer = new PathNormalizer(@"C:\actions\work", [@"C:\actions\work"], FileList);

        var (path, matched) = normalizer.Normalize(@"C:\actions\work\src\Calculator.cs");

        path.Should().Be("src/Calculator.cs");
        matched.Should().BeTrue();
    }

    [Fact]
    public void Suffix_matches_paths_with_unstated_source_roots()
    {
        var normalizer = new PathNormalizer(null, [], FileList);

        // JaCoCo-style: package path without the src/main/java root.
        var (path, matched) = normalizer.Normalize("com/acme/App.java");

        path.Should().Be("src/main/java/com/acme/App.java");
        matched.Should().BeTrue();
    }

    [Fact]
    public void Ambiguous_suffix_matches_stay_unmatched()
    {
        var normalizer = new PathNormalizer(null, [], FileList);

        // util.ts exists in src/ and tools/ — a bare "util.ts" is ambiguous.
        var (_, matched) = normalizer.Normalize("util.ts");

        matched.Should().BeFalse();
    }

    [Fact]
    public void Unresolvable_paths_are_returned_unmatched_not_dropped()
    {
        var normalizer = new PathNormalizer("/workspace", [], FileList);

        var (path, matched) = normalizer.Normalize("/somewhere/else/Ghost.cs");

        matched.Should().BeFalse();
        path.Should().EndWith("Ghost.cs");
    }

    [Fact]
    public void Without_a_file_list_relative_paths_pass_and_absolute_paths_flag()
    {
        var normalizer = new PathNormalizer("/workspace", [], []);

        normalizer.Normalize("/workspace/src/a.cs").Should().Be(("src/a.cs", true));
        normalizer.Normalize("/other/root/a.cs").Matched.Should().BeFalse();
        normalizer.Normalize("src/a.cs").Should().Be(("src/a.cs", true));
    }

    [Fact]
    public void Case_insensitive_exact_match_returns_the_repo_casing()
    {
        var normalizer = new PathNormalizer(null, [], FileList);

        var (path, matched) = normalizer.Normalize("SRC/calculator.CS");

        path.Should().Be("src/Calculator.cs");
        matched.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // #417's separator table. Explicitly NOT a bug fix: the #415 A/B proved the
    // separators were never the cause — the same absolute backslash paths resolved
    // all 79 files once the BOM was gone. These pin behaviour that already worked,
    // so the next investigator reads a test instead of re-deriving it.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_windows_absolute_report_path_resolves()
    {
        var normalizer = new PathNormalizer(@"D:\a\repo\repo", [], FileList);

        normalizer.Normalize(@"D:\a\repo\repo\src\Calculator.cs")
            .Should().Be(("src/Calculator.cs", true));
    }

    [Fact]
    public void A_posix_absolute_report_path_resolves()
    {
        var normalizer = new PathNormalizer("/home/runner/work/repo/repo", [], FileList);

        normalizer.Normalize("/home/runner/work/repo/repo/src/Calculator.cs")
            .Should().Be(("src/Calculator.cs", true));
    }

    [Fact]
    public void Mixed_separators_within_one_report_all_resolve()
    {
        var normalizer = new PathNormalizer(@"D:\a\repo\repo", [], FileList);

        normalizer.Normalize(@"D:\a\repo\repo\src\Calculator.cs").Should().Be(("src/Calculator.cs", true));
        normalizer.Normalize("D:/a/repo/repo/src/util.ts").Should().Be(("src/util.ts", true));
        normalizer.Normalize(@"src\Calculator.cs").Should().Be(("src/Calculator.cs", true));
    }

    [Fact]
    public void An_already_relative_path_passes_through_untouched()
    {
        var normalizer = new PathNormalizer("/home/runner/work/repo/repo", [], FileList);

        normalizer.Normalize("src/Calculator.cs").Should().Be(("src/Calculator.cs", true));
    }

    /// <summary>
    /// The one real edge case raised on #417: a backslash is a legal character in a
    /// POSIX filename, so <c>src/weird\name.cs</c> can genuinely be committed.
    /// Unifying first would collide it with <c>src/weird/name.cs</c> and resolve to
    /// the wrong file — silently, because both are "matched". The literal pass runs
    /// first, so it resolves to itself.
    /// </summary>
    [Fact]
    public void A_posix_filename_containing_a_backslash_resolves_to_itself()
    {
        string[] fileList = [@"src/weird\name.cs", "src/weird/name.cs"];
        var normalizer = new PathNormalizer("/home/runner/work/repo/repo", [], fileList);

        normalizer.Normalize(@"src/weird\name.cs").Should().Be((@"src/weird\name.cs", true));
        normalizer.Normalize("src/weird/name.cs").Should().Be(("src/weird/name.cs", true));
    }

    /// <summary>
    /// The literal pass must not shadow the common case. A Windows absolute path
    /// cannot match the repository's file list literally, so it falls straight through
    /// to the pipeline that has always handled it.
    /// </summary>
    [Fact]
    public void The_literal_pass_does_not_shadow_a_windows_absolute_path()
    {
        string[] fileList = [@"src/weird\name.cs", "src/Calculator.cs"];
        var normalizer = new PathNormalizer(@"D:\a\repo\repo", [], fileList);

        normalizer.Normalize(@"D:\a\repo\repo\src\Calculator.cs")
            .Should().Be(("src/Calculator.cs", true));
    }
}
