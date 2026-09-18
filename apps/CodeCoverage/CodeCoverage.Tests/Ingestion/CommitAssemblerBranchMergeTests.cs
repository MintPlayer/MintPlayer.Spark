using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The build-to-build assembly path (CommitAssembler's MergeInto(FileCoverage,
/// FileCoverage)) carried the same cross-format drop as the session path and had
/// no test at all: every assembler fixture is lcov with DA: records only, so
/// nothing exercised branch data through it.
/// </summary>
public class CommitAssemblerBranchMergeTests
{
    private static FileCoverage Stored(int line, int? hits, Action<ParsedFile> branches)
    {
        var parsed = new ParsedFile { RawPath = "src/a.ts" };
        parsed.AddLine(line, hits);
        branches(parsed);
        parsed.ResolveStatuses();

        var stored = new FileCoverage { BuildId = "b", Path = "src/a.ts" };
        CoverageMerger.MergeInto(stored, parsed);
        return stored;
    }

    [Fact]
    public void Two_builds_in_different_formats_both_contribute()
    {
        var lcovBuild = Stored(5, 1, p =>
        {
            p.AddBranchArm(5, "0:0", taken: true);
            p.AddBranchArm(5, "0:1", taken: false);
        });
        var coberturaBuild = Stored(5, 2, p => p.AddBranchCount(5, covered: 2, total: 2));

        var target = CoverageMerger.Clone(lcovBuild);
        CoverageMerger.MergeInto(target, coberturaBuild);

        target.Branches.Single().Covered.Should().Be(2, "the second build proves both arms were taken");
        target.Branches.Single().Arity.Should().Be(2);
        target.Lines.Single().Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void Assembly_merge_is_order_independent()
    {
        var lcovBuild = Stored(5, 1, p =>
        {
            p.AddBranchArm(5, "0:0", taken: true);
            p.AddBranchArm(5, "0:1", taken: false);
        });
        var jacocoBuild = Stored(5, null, p => p.AddBranchCount(5, covered: 1, total: 2));

        var ab = CoverageMerger.Clone(lcovBuild);
        CoverageMerger.MergeInto(ab, jacocoBuild);

        var ba = CoverageMerger.Clone(jacocoBuild);
        CoverageMerger.MergeInto(ba, lcovBuild);

        ab.Branches.Should().BeEquivalentTo(ba.Branches);
        ab.Lines.Should().BeEquivalentTo(ba.Lines);
    }

    [Fact]
    public void Arm_identity_survives_a_round_trip_through_a_stored_document()
    {
        // MergeInto(FileCoverage, FileCoverage) rebuilds a ParsedFile from the
        // stored shape; arm keys must come back as arms, not as a floor, or the
        // assembled copy would silently lose the ability to union with a later
        // report covering the other arm.
        var first = Stored(5, 1, p =>
        {
            p.AddBranchArm(5, "0:0", taken: true);
            p.AddBranchArm(5, "0:1", taken: false);
        });
        var second = Stored(5, 1, p =>
        {
            p.AddBranchArm(5, "0:0", taken: false);
            p.AddBranchArm(5, "0:1", taken: true);
        });

        var target = CoverageMerger.Clone(first);
        CoverageMerger.MergeInto(target, second);

        target.Branches.Single().TakenArms.Should().BeEquivalentTo(["0:0", "0:1"]);
        target.Branches.Single().Floor.Should().Be(0);
        target.Lines.Single().Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void Clone_deep_copies_the_arm_list()
    {
        var source = Stored(5, 1, p => p.AddBranchArm(5, "0:0", taken: true));

        var clone = CoverageMerger.Clone(source);
        clone.Branches.Single().TakenArms.Add("0:1");

        source.Branches.Single().TakenArms.Should().ContainSingle("the assembler never shares list instances");
    }
}
