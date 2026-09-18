using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

public class LcovParserTests
{
    private readonly LcovParser parser = new();

    private const string Sample = """
        TN:
        SF:/home/runner/work/repo/repo/src/calc.ts
        FN:1,add
        FNDA:3,add
        FNF:1
        FNH:1
        DA:1,3
        DA:2,3
        DA:4,0
        BRDA:2,0,0,2
        BRDA:2,0,1,0
        LF:3
        LH:2
        BRF:2
        BRH:1
        end_of_record
        SF:src\util.ts
        DA:10,1
        end_of_record
        """;

    [Fact]
    public void CanParse_recognizes_TN_and_SF_starts()
    {
        parser.CanParse(Sample).Should().BeTrue();
        parser.CanParse("SF:foo.c\nend_of_record").Should().BeTrue();
        parser.CanParse("<coverage/>").Should().BeFalse();
        parser.CanParse("").Should().BeFalse();
    }

    [Fact]
    public void Parses_lines_branches_and_statuses()
    {
        var result = parser.Parse(Sample);

        result.Files.Should().HaveCount(2);
        var calc = result.Files[0];
        calc.RawPath.Should().Be("/home/runner/work/repo/repo/src/calc.ts");
        calc.Lines.Should().HaveCount(3);
        calc.Lines[1].Hits.Should().Be(3);
        calc.Lines[4].Hits.Should().Be(0);
        calc.Lines[4].Status.Should().Be(LineStatus.NotCovered);

        // Line 2 executed but one of its two branches was never taken → partial.
        calc.Lines[2].Status.Should().Be(LineStatus.PartiallyCovered);
        calc.Branches[2].Arity.Should().Be(2);
        calc.Branches[2].TakenArms.Should().BeEquivalentTo(["0:0"]);
        calc.Branches[2].Covered.Should().Be(1);

        // Fully covered, no branches.
        calc.Lines[1].Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void Handles_lcov2_block_prefixes_and_dash_taken()
    {
        const string lcov2 = """
            SF:main.c
            DA:5,1
            BRDA:5,e0,0,-
            BRDA:5,f0,1,4
            end_of_record
            """;

        var file = parser.Parse(lcov2).Files.Single();
        // '-' means the enclosing block never executed; either way the arm was
        // not taken, so the line is partial.
        file.Branches[5].Arity.Should().Be(2);
        file.Branches[5].TakenArms.Should().BeEquivalentTo(["f0:1"]);
        file.Lines[5].Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Lcov2_block_markers_stay_part_of_the_arm_identity()
    {
        // e0 (exception) and 0 are different blocks. Trimming the marker, as the
        // parser used to, collides them — and under a set model that merges two
        // arms which are not the same arm, over-reporting coverage.
        const string collide = """
            SF:main.c
            DA:5,1
            BRDA:5,0,0,1
            BRDA:5,e0,0,-
            end_of_record
            """;

        var file = parser.Parse(collide).Files.Single();
        file.Branches[5].Arity.Should().Be(2);
        file.Branches[5].TakenArms.Should().BeEquivalentTo(["0:0"]);
        file.Lines[5].Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Duplicate_DA_records_take_the_max_never_the_sum()
    {
        // Was Accumulates_duplicate_DA_records, which pinned the sum. Summing
        // duplicates makes a report split across two uploads differ from the
        // same data in one upload, and CoverageMerger has always maxed.
        const string dup = """
            SF:a.c
            DA:1,2
            DA:1,3
            end_of_record
            """;

        var file = parser.Parse(dup).Files.Single();
        file.Lines[1].Hits.Should().Be(3);
    }

    [Fact]
    public void Tolerates_missing_trailing_end_of_record()
    {
        var file = parser.Parse("SF:a.c\nDA:1,1").Files.Single();
        file.Lines[1].Hits.Should().Be(1);
    }
}
