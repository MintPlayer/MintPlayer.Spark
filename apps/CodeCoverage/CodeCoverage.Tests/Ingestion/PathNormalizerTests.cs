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
}
