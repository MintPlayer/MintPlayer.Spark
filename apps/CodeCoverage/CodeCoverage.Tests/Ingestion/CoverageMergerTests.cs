using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

public class CoverageMergerTests
{
    private static ParsedFile ParsedWith(params (int Line, int? Hits)[] lines)
    {
        var file = new ParsedFile { RawPath = "x" };
        foreach (var (line, hits) in lines)
            file.AddLine(line, hits);
        file.ResolveStatuses();
        return file;
    }

    /// <summary>An identity-carrying report (lcov, istanbul, Cobertura &lt;conditions&gt;).</summary>
    private static ParsedFile WithArms(int line, int? hits, params (string Arm, bool Taken)[] arms)
    {
        var file = new ParsedFile { RawPath = "x" };
        file.AddLine(line, hits);
        foreach (var (arm, taken) in arms)
            file.AddBranchArm(line, arm, taken);
        file.ResolveStatuses();
        return file;
    }

    /// <summary>A count-only report (Cobertura condition-coverage, JaCoCo, Clover).</summary>
    private static ParsedFile WithCount(int line, int? hits, int covered, int total)
    {
        var file = new ParsedFile { RawPath = "x" };
        file.AddLine(line, hits);
        file.AddBranchCount(line, covered, total);
        file.ResolveStatuses();
        return file;
    }

    [Fact]
    public void Merge_takes_max_of_hits_never_sum()
    {
        var target = new FileCoverage { BuildId = "b", Path = "x" };

        CoverageMerger.MergeInto(target, ParsedWith((1, 3), (2, 0)));
        CoverageMerger.MergeInto(target, ParsedWith((1, 3), (2, 0)));   // identical re-upload

        target.Lines.Single(l => l.Number == 1).Hits.Should().Be(3, "a re-uploaded report must not inflate counts");
        target.Lines.Single(l => l.Number == 2).Hits.Should().Be(0);
    }

    [Fact]
    public void Merge_is_idempotent_and_order_independent_for_status()
    {
        var shard1 = ParsedWith((1, 5), (2, 0));
        var shard2 = ParsedWith((1, 0), (2, 2));

        var ab = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(ab, shard1);
        CoverageMerger.MergeInto(ab, shard2);

        var ba = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(ba, shard2);
        CoverageMerger.MergeInto(ba, shard1);

        ab.Lines.Should().BeEquivalentTo(ba.Lines);
        ab.Lines.Should().OnlyContain(l => l.Status == LineStatus.Covered);
    }

    [Fact]
    public void Partial_line_becomes_covered_when_another_session_takes_the_remaining_branch()
    {
        var session1 = WithArms(5, 1, ("0:0", true), ("0:1", false));
        var session2 = WithArms(5, 1, ("0:0", false), ("0:1", true));

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, session1);
        target.Lines.Single().Status.Should().Be(LineStatus.PartiallyCovered);

        CoverageMerger.MergeInto(target, session2);
        target.Lines.Single().Status.Should().Be(LineStatus.Covered, "the union of both sessions takes every branch");
        target.Branches.Single().Covered.Should().Be(2);
    }

    [Fact]
    public void Null_hits_do_not_erase_known_counts()
    {
        var withCounts = ParsedWith((1, 7));
        var withoutCounts = new ParsedFile { RawPath = "x" };
        withoutCounts.AddLine(1, null);
        withoutCounts.ResolveStatuses();

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, withCounts);
        CoverageMerger.MergeInto(target, withoutCounts);

        target.Lines.Single().Hits.Should().Be(7);
    }

    [Fact]
    public void Count_only_report_contributes_its_floor_to_an_identified_set()
    {
        // This replaces Branch_detail_never_merges_across_formats, which asserted
        // the bug as a feature: a foreign-format report used to contribute
        // nothing at all, not even the floor it genuinely established.
        var lcov = WithArms(5, 1, ("0:0", true), ("0:1", false));
        var cobertura = WithCount(5, 2, covered: 1, total: 2);

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, lcov);
        CoverageMerger.MergeInto(target, cobertura);

        var branches = target.Branches.Single();
        branches.TakenArms.Should().ContainSingle().Which.Should().Be("0:0");
        branches.Floor.Should().Be(1, "the count-only report asserts at least one arm was taken");
        branches.Covered.Should().Be(1, "it cannot say WHICH arm, so it never exceeds the named set");
        branches.Arity.Should().Be(2);

        target.Lines.Single().Hits.Should().Be(2, "line data still merges across formats");
        target.Lines.Single().Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Count_only_report_can_exceed_the_identified_set()
    {
        // The floor is real information: a Cobertura report saying 2/2 proves
        // both arms were taken even though lcov only ever named one of them.
        var lcov = WithArms(5, 1, ("0:0", true), ("0:1", false));
        var cobertura = WithCount(5, 1, covered: 2, total: 2);

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, lcov);
        CoverageMerger.MergeInto(target, cobertura);

        target.Branches.Single().Covered.Should().Be(2);
        target.Lines.Single().Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void Cross_format_merge_is_order_independent()
    {
        var lcov = WithArms(5, 1, ("0:0", true), ("0:1", false));
        var cobertura = WithCount(5, 2, covered: 1, total: 2);

        var ab = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(ab, lcov);
        CoverageMerger.MergeInto(ab, cobertura);

        var ba = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(ba, cobertura);
        CoverageMerger.MergeInto(ba, lcov);

        ab.Branches.Should().BeEquivalentTo(ba.Branches);
        ab.Lines.Should().BeEquivalentTo(ba.Lines);
    }

    [Fact]
    public void Arity_grows_to_the_largest_any_report_claimed()
    {
        // A later report seeing more arms must not be truncated to the first
        // report's arity — the old row-counting model pinned it.
        var narrow = WithArms(5, 1, ("0:0", true), ("0:1", true));
        var wide = WithCount(5, 1, covered: 2, total: 4);

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, narrow);
        CoverageMerger.MergeInto(target, wide);

        target.Branches.Single().Arity.Should().Be(4);
        target.Branches.Single().Covered.Should().Be(2);
        target.Lines.Single().Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Summarize_counts_partial_as_covered_and_branches_taken()
    {
        var file = new FileCoverage
        {
            BuildId = "b",
            Path = "x",
            Lines =
            [
                new LineCoverage { Number = 1, Hits = 2, Status = LineStatus.Covered },
                new LineCoverage { Number = 2, Hits = 1, Status = LineStatus.PartiallyCovered },
                new LineCoverage { Number = 3, Hits = 0, Status = LineStatus.NotCovered },
            ],
            Branches =
            [
                new LineBranchCoverage { Line = 2, Arity = 2, TakenArms = ["0:0"] },
            ],
        };

        var summary = CoverageMerger.Summarize([file]);

        summary.FilesCount.Should().Be(1);
        summary.LinesCoverable.Should().Be(3);
        summary.LinesCovered.Should().Be(2, "an executed-but-partial line still executed");
        summary.BranchesTotal.Should().Be(2);
        summary.BranchesCovered.Should().Be(1);
    }

    [Fact]
    public void Summarize_uses_the_floor_when_no_arm_was_named()
    {
        var file = new FileCoverage
        {
            BuildId = "b",
            Path = "x",
            Lines = [new LineCoverage { Number = 2, Hits = 1, Status = LineStatus.PartiallyCovered }],
            Branches = [new LineBranchCoverage { Line = 2, Arity = 4, Floor = 3 }],
        };

        var summary = CoverageMerger.Summarize([file]);

        summary.BranchesTotal.Should().Be(4);
        summary.BranchesCovered.Should().Be(3);
    }
}
