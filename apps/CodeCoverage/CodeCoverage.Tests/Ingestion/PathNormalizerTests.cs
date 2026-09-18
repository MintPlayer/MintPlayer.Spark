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
    /// The deliberate exception to <see cref="A_backslash_never_reaches_the_stored_path"/>,
    /// recorded so it is a decision rather than a surprise.
    ///
    /// <para>FR-12 exists so that a repository genuinely containing
    /// <c>src/weird\name.cs</c> does not silently resolve to <c>src/weird/name.cs</c>.
    /// The price is that this one path is stored with its backslash — which is right:
    /// it is the file's actual name, it is what <c>git ls-files</c> reports, and the
    /// document id hashes it consistently in both directions, so it round-trips.</para>
    ///
    /// <para><b>It does make one previously-unreachable sharp edge reachable.</b>
    /// <c>Services/GitHubContentService.cs:57</c> percent-encodes the path and
    /// un-escapes only <c>%2F</c>, so a stored backslash arrives as <c>%5C</c> and the
    /// raw.githubusercontent fetch 404s — the file page would fail to show source.
    /// Client-side, the breadcrumb and file-name splits on <c>'/'</c> render it as one
    /// segment. Both are cosmetic against a file that is vanishingly rare and was
    /// previously resolved to the <i>wrong file's</i> coverage, which is worse. Noted
    /// here rather than fixed speculatively; measured production occurrences: zero.</para>
    /// </summary>
    [Fact]
    public void A_genuinely_backslashed_repo_file_is_the_one_path_that_keeps_its_backslash()
    {
        string[] fileList = [@"src/weird\name.cs"];
        var normalizer = new PathNormalizer("/home/runner/work/repo/repo", [], fileList);

        var (path, matched) = normalizer.Normalize(@"src/weird\name.cs");

        matched.Should().BeTrue();
        path.Should().Be(@"src/weird\name.cs");
    }

    /// <summary>
    /// THE INVARIANT BEHIND "no migration is needed" (#417, M0b).
    ///
    /// <para>Measured against production on 2026-09-18: 194,548 <c>FileCoverage.Path</c>
    /// values and 193,420 <c>BuildTreeSummary</c> paths, <b>zero</b> containing a
    /// backslash. That is the whole database, not a sample. This test is what keeps
    /// it true — the stored path is hashed into the document id
    /// (<c>{buildId}/files/{SHA256(path)}</c>), so a backslash reaching storage would
    /// split one file across two unreachable documents with no way back.</para>
    ///
    /// <para>Both exits matter. The unmatched exit is the easy one to regress, because
    /// it is the path that gets returned when nothing resolved — and an unmatched file
    /// is still stored.</para>
    ///
    /// <para><b>There is exactly one deliberate exception, and FR-12 created it</b>: a
    /// repository that genuinely contains <c>src/weird\name.cs</c> stores that path,
    /// backslash and all, because it is the file's real name. See
    /// <see cref="A_genuinely_backslashed_repo_file_is_the_one_path_that_keeps_its_backslash"/>
    /// for why that is correct and what it costs.</para>
    /// </summary>
    [Theory]
    [InlineData(@"D:\a\repo\repo\src\Calculator.cs")]     // matched, via the root strip
    [InlineData(@"src\Calculator.cs")]                     // matched, already relative
    [InlineData(@"D:\somewhere\else\Unknown.cs")]          // unmatched, absolute
    [InlineData(@"nope\not\in\the\repo.cs")]               // unmatched, relative
    [InlineData(@"C:\actions\work\src\deep\Thing.cs")]     // unmatched, different root
    public void A_backslash_never_reaches_the_stored_path(string rawPath)
    {
        var normalizer = new PathNormalizer(@"D:\a\repo\repo", [], FileList);

        var (path, _) = normalizer.Normalize(rawPath);

        path.Should().NotContain(@"\");
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
