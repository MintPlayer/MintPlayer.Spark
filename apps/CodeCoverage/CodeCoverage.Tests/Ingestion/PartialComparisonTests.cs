using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The #11 N2 contract, pinned as pure computation: the scoped baseline is
/// like-for-like (base restricted to measured paths), the projection is the
/// base patched with the measured files and pruned of deleted ones, and every
/// asymmetry (new file, deleted file, missing file list) lands on the side the
/// design says it must.
/// </summary>
public class PartialComparisonTests
{
    private static BuildTreeSummary Tree(params (string Path, int Covered, int Coverable)[] files) => new()
    {
        Files = [.. files.Select(f => new TreeFileSummary { Path = f.Path, LinesCovered = f.Covered, LinesCoverable = f.Coverable })],
    };

    [Fact]
    public void Scoped_baseline_restricts_the_base_to_the_measured_paths()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10), ("libs/b/y.ts", 90, 100), ("libs/c/z.ts", 5, 5));
        var headTree = Tree(("libs/a/x.ts", 9, 10));

        var result = PartialComparison.Compute(headTree, baseTree, ["libs/a/x.ts", "libs/b/y.ts", "libs/c/z.ts"]);

        result.ScopedBaseline.LinesCovered.Should().Be(8);
        result.ScopedBaseline.LinesCoverable.Should().Be(10);
        result.FilesInScope.Should().Be(1);
    }

    [Fact]
    public void Projection_overwrites_measured_files_and_keeps_the_rest()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10), ("libs/b/y.ts", 90, 100));
        var headTree = Tree(("libs/a/x.ts", 10, 10));

        var result = PartialComparison.Compute(headTree, baseTree, ["libs/a/x.ts", "libs/b/y.ts"]);

        result.Projection.LinesCovered.Should().Be(10 + 90);
        result.Projection.LinesCoverable.Should().Be(10 + 100);
        result.Projection.FilesCount.Should().Be(2);
    }

    [Fact]
    public void A_file_new_in_the_head_counts_toward_head_and_projection_but_never_the_baseline()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10));
        var headTree = Tree(("libs/a/x.ts", 8, 10), ("libs/a/new.ts", 3, 4));

        var result = PartialComparison.Compute(headTree, baseTree, ["libs/a/x.ts", "libs/a/new.ts"]);

        result.ScopedBaseline.LinesCoverable.Should().Be(10, "the base never measured the new file");
        result.Projection.LinesCoverable.Should().Be(14);
        result.Projection.FilesCount.Should().Be(2);
    }

    [Fact]
    public void A_file_deleted_by_the_pr_is_pruned_from_the_projection()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10), ("libs/gone/old.ts", 50, 50));
        var headTree = Tree(("libs/a/x.ts", 9, 10));

        var result = PartialComparison.Compute(headTree, baseTree, ["libs/a/x.ts"]);

        result.Projection.LinesCoverable.Should().Be(10);
        result.PrunedFiles.Should().Be(1);
        result.ScopedBaseline.LinesCoverable.Should().Be(10, "deleted files were never in the measured scope");
    }

    [Fact]
    public void Without_a_file_list_nothing_is_pruned()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10), ("libs/gone/old.ts", 50, 50));
        var headTree = Tree(("libs/a/x.ts", 9, 10));

        var result = PartialComparison.Compute(headTree, baseTree, headFileList: null);

        result.PrunedFiles.Should().Be(0);
        result.Projection.LinesCoverable.Should().Be(60, "best effort: keep the base entry rather than guess");
    }

    [Fact]
    public void A_measured_file_is_never_pruned_even_when_the_file_list_omits_it()
    {
        // An unmatched-but-measured path won't appear in git ls-files output;
        // pruning it would contradict the measurement.
        var baseTree = Tree(("weird/path.ts", 5, 10));
        var headTree = Tree(("weird/path.ts", 7, 10));

        var result = PartialComparison.Compute(headTree, baseTree, ["libs/a/x.ts"]);

        result.PrunedFiles.Should().Be(0);
        result.Projection.LinesCovered.Should().Be(7);
    }

    [Fact]
    public void Measuring_nothing_new_projects_exactly_the_base_total()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10), ("libs/b/y.ts", 90, 100));
        var headTree = new BuildTreeSummary();

        var result = PartialComparison.Compute(headTree, baseTree, ["libs/a/x.ts", "libs/b/y.ts"]);

        result.Projection.LinesCovered.Should().Be(98);
        result.Projection.LinesCoverable.Should().Be(110);
        result.ScopedBaseline.LinesCoverable.Should().Be(0);
        result.FilesInScope.Should().Be(0);
    }

    [Fact]
    public void Backslash_file_lists_still_match_normalized_tree_paths()
    {
        var baseTree = Tree(("libs/a/x.ts", 8, 10), ("libs/gone/old.ts", 1, 1));
        var headTree = Tree(("libs/a/x.ts", 9, 10));

        var result = PartialComparison.Compute(headTree, baseTree, [@"libs\a\x.ts"]);

        result.PrunedFiles.Should().Be(1, "only the genuinely deleted file is pruned");
        result.Projection.LinesCoverable.Should().Be(10);
    }
}
