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

    [Fact]
    public void Merge_takes_max_of_hits_never_sum()
    {
        var target = new FileCoverage { BuildId = "b", Path = "x" };

        CoverageMerger.MergeInto(target, ParsedWith((1, 3), (2, 0)), "lcov");
        CoverageMerger.MergeInto(target, ParsedWith((1, 3), (2, 0)), "lcov");   // identical re-upload

        target.Lines.Single(l => l.Number == 1).Hits.Should().Be(3, "a re-uploaded report must not inflate counts");
        target.Lines.Single(l => l.Number == 2).Hits.Should().Be(0);
    }

    [Fact]
    public void Merge_is_idempotent_and_order_independent_for_status()
    {
        var shard1 = ParsedWith((1, 5), (2, 0));
        var shard2 = ParsedWith((1, 0), (2, 2));

        var ab = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(ab, shard1, "lcov");
        CoverageMerger.MergeInto(ab, shard2, "lcov");

        var ba = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(ba, shard2, "lcov");
        CoverageMerger.MergeInto(ba, shard1, "lcov");

        ab.Lines.Should().BeEquivalentTo(ba.Lines);
        ab.Lines.Should().OnlyContain(l => l.Status == LineStatus.Covered);
    }

    [Fact]
    public void Partial_line_becomes_covered_when_another_session_takes_the_remaining_branch()
    {
        var session1 = new ParsedFile { RawPath = "x" };
        session1.AddLine(5, 1);
        session1.AddBranch(5, "0", "0", 1);
        session1.AddBranch(5, "0", "1", 0);
        session1.ResolveStatuses();

        var session2 = new ParsedFile { RawPath = "x" };
        session2.AddLine(5, 1);
        session2.AddBranch(5, "0", "0", 0);
        session2.AddBranch(5, "0", "1", 1);
        session2.ResolveStatuses();

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, session1, "lcov");
        target.Lines.Single().Status.Should().Be(LineStatus.PartiallyCovered);

        CoverageMerger.MergeInto(target, session2, "lcov");
        target.Lines.Single().Status.Should().Be(LineStatus.Covered, "the union of both sessions takes every branch");
        target.Branches.Should().OnlyContain(b => b.Taken == 1);
    }

    [Fact]
    public void Null_hits_do_not_erase_known_counts()
    {
        var withCounts = ParsedWith((1, 7));
        var withoutCounts = new ParsedFile { RawPath = "x" };
        withoutCounts.AddLine(1, null);
        withoutCounts.ResolveStatuses();

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, withCounts, "lcov");
        CoverageMerger.MergeInto(target, withoutCounts, "lcov");

        target.Lines.Single().Hits.Should().Be(7);
    }

    [Fact]
    public void Branch_detail_never_merges_across_formats()
    {
        // lcov reports real branch ids; Cobertura synthesizes "0"/index edges.
        // The keys collide by accident, so max-merging them would invent
        // coverage: here it would flip both edges to taken.
        var lcov = new ParsedFile { RawPath = "x" };
        lcov.AddLine(5, 1);
        lcov.AddBranch(5, "0", "0", 1);
        lcov.AddBranch(5, "0", "1", 0);
        lcov.ResolveStatuses();

        var cobertura = new ParsedFile { RawPath = "x" };
        cobertura.AddLine(5, 2);
        cobertura.AddBranch(5, "0", "0", 0);
        cobertura.AddBranch(5, "0", "1", 1);
        cobertura.ResolveStatuses();

        var target = new FileCoverage { BuildId = "b", Path = "x" };
        CoverageMerger.MergeInto(target, lcov, "lcov");
        CoverageMerger.MergeInto(target, cobertura, "cobertura");

        target.BranchFormat.Should().Be("lcov");
        target.Branches.Count(b => b.Taken > 0).Should().Be(1, "the foreign format's edges must not merge in");
        target.Lines.Single().Hits.Should().Be(2, "line data still merges across formats");
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
                new BranchCoverage { Line = 2, BlockId = "0", BranchId = "0", Taken = 1 },
                new BranchCoverage { Line = 2, BlockId = "0", BranchId = "1", Taken = 0 },
            ],
        };

        var summary = CoverageMerger.Summarize([file]);

        summary.FilesCount.Should().Be(1);
        summary.LinesCoverable.Should().Be(3);
        summary.LinesCovered.Should().Be(2, "an executed-but-partial line still executed");
        summary.BranchesTotal.Should().Be(2);
        summary.BranchesCovered.Should().Be(1);
    }
}
