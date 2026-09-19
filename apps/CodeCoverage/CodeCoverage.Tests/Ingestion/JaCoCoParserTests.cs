using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

public class JaCoCoParserTests
{
    private readonly JaCoCoParser parser = new();

    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
        <report name="demo">
          <sessioninfo id="host-1" start="1700000000000" dump="1700000001000"/>
          <package name="com/example">
            <class name="com/example/Calculator" sourcefilename="Calculator.java">
              <method name="add" desc="(II)I" line="10">
                <counter type="INSTRUCTION" missed="0" covered="4"/>
              </method>
            </class>
            <sourcefile name="Calculator.java">
              <line nr="10" mi="0" ci="4" mb="0" cb="0"/>
              <line nr="12" mi="0" ci="6" mb="1" cb="1"/>
              <line nr="14" mi="3" ci="0" mb="0" cb="0"/>
              <counter type="LINE" missed="1" covered="2"/>
            </sourcefile>
            <sourcefile name="Util.java">
              <line nr="5" mi="0" ci="2" mb="0" cb="2"/>
            </sourcefile>
          </package>
          <package name="">
            <sourcefile name="Root.java">
              <line nr="1" mi="0" ci="1" mb="0" cb="0"/>
            </sourcefile>
          </package>
        </report>
        """;

    [Fact]
    public void CanParse_requires_report_root_with_jacoco_markers()
    {
        parser.CanParse(Sample).Should().BeTrue();
        parser.CanParse("<coverage line-rate=\"1\"/>").Should().BeFalse();
        parser.CanParse("TN:\nSF:x").Should().BeFalse();
    }

    [Fact]
    public void Factory_resolves_jacoco()
    {
        new CoverageParserFactory().Resolve(Sample).Should().BeOfType<JaCoCoParser>();
    }

    [Fact]
    public void Paths_join_package_and_sourcefile()
    {
        var result = parser.Parse(Sample);

        result.Files.Select(f => f.RawPath).Should().BeEquivalentTo(
            ["com/example/Calculator.java", "com/example/Util.java", "Root.java"]);
    }

    [Fact]
    public void Executed_lines_have_null_hits_and_missed_lines_zero()
    {
        var result = parser.Parse(Sample);
        var file = result.Files.Single(f => f.RawPath == "com/example/Calculator.java");

        // JaCoCo has no execution counts: covered lines carry Hits = null
        // (executed, count unknown), unexecuted lines a genuine 0.
        file.Lines[10].Hits.Should().NotHaveValue();
        file.Lines[10].Status.Should().Be(LineStatus.Covered);

        file.Lines[14].Hits.Should().Be(0);
        file.Lines[14].Status.Should().Be(LineStatus.NotCovered);
    }

    [Fact]
    public void Missed_branches_make_a_line_partially_covered()
    {
        var result = parser.Parse(Sample);
        var file = result.Files.Single(f => f.RawPath == "com/example/Calculator.java");

        file.Lines[12].Status.Should().Be(LineStatus.PartiallyCovered);
        // mb/cb are counts with no arm identity — a floor, never named arms.
        file.Branches[12].Arity.Should().Be(2);
        file.Branches[12].Floor.Should().Be(1);
        file.Branches[12].TakenArms.Should().BeEmpty();
    }

    [Fact]
    public void Fully_taken_branches_stay_covered()
    {
        var result = parser.Parse(Sample);
        var file = result.Files.Single(f => f.RawPath == "com/example/Util.java");

        file.Lines[5].Status.Should().Be(LineStatus.Covered);
        file.Branches[5].Arity.Should().Be(2);
        file.Branches[5].Covered.Should().Be(2);
        file.Branches[5].IsPartial.Should().BeFalse();
    }

    [Fact]
    public void An_instruction_partial_line_is_partial_even_with_no_branches()
    {
        // JaCoCo's own colouring: ci==0 red, ci>0 && mi>0 YELLOW, mi==0 green.
        // mi was never read, so every yellow line was stored as fully covered.
        const string InstructionPartial = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="r">
              <package name="com/example">
                <sourcefile name="A.java">
                  <line nr="7" mi="2" ci="4" mb="0" cb="0"/>
                  <line nr="8" mi="0" ci="3" mb="0" cb="0"/>
                  <line nr="9" mi="5" ci="0" mb="0" cb="0"/>
                </sourcefile>
              </package>
            </report>
            """;

        var file = parser.Parse(InstructionPartial).Files.Single();

        file.Lines[7].Status.Should().Be(LineStatus.PartiallyCovered);
        file.Lines[7].InstructionsMissed.Should().Be(2);
        file.Lines[8].Status.Should().Be(LineStatus.Covered);
        file.Lines[9].Status.Should().Be(LineStatus.NotCovered);

        // No branch data was invented to express it.
        file.Branches.Should().BeEmpty();
    }

    [Fact]
    public void A_second_report_covering_the_missed_instructions_clears_the_partial()
    {
        // MIN, not MAX: another run executing those instructions proves they are
        // reachable, so the union is fully covered.
        const string Missed = """
            <report name="r"><package name="p"><sourcefile name="A.java">
              <line nr="7" mi="2" ci="4" mb="0" cb="0"/>
            </sourcefile></package></report>
            """;
        const string Complete = """
            <report name="r"><package name="p"><sourcefile name="A.java">
              <line nr="7" mi="0" ci="6" mb="0" cb="0"/>
            </sourcefile></package></report>
            """;

        var file = new FileCoverage { BuildId = "b", Path = "p/A.java" };
        CoverageMerger.MergeInto(file, parser.Parse(Missed).Files.Single());
        CoverageMerger.MergeInto(file, parser.Parse(Complete).Files.Single());

        var line = file.Lines.Single(l => l.Number == 7);
        line.InstructionsMissed.Should().Be(0);
        line.Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void A_report_that_counts_no_instructions_does_not_clear_the_partial()
    {
        // null means "made no claim", so it must not be read as "missed zero".
        var file = new FileCoverage { BuildId = "b", Path = "p/A.java" };
        CoverageMerger.MergeInto(file, parser.Parse("""
            <report name="r"><package name="p"><sourcefile name="A.java">
              <line nr="7" mi="2" ci="4" mb="0" cb="0"/>
            </sourcefile></package></report>
            """).Files.Single());

        // An lcov-shaped contribution: executed, no instruction counts at all.
        var lcovLike = new ParsedFile { RawPath = "p/A.java" };
        lcovLike.AddLine(7, 3);
        lcovLike.ResolveStatuses();
        CoverageMerger.MergeInto(file, lcovLike);

        var line = file.Lines.Single(l => l.Number == 7);
        line.InstructionsMissed.Should().Be(2);
        line.Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Packages_nested_in_a_group_are_found()
    {
        // report.dtd: <!ELEMENT report (sessioninfo*,(group|package)*,counter*)>
        // and <!ELEMENT group (group|package)*,counter*>. jacoco:report-aggregate
        // emits exactly this for a multi-module build; reading only direct
        // children parsed it to zero files and the upload was rejected as
        // "noFiles" rather than ingested.
        const string Aggregate = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="aggregate">
              <group name="module-a">
                <package name="com/example">
                  <sourcefile name="A.java">
                    <line nr="3" mi="0" ci="2" mb="1" cb="1"/>
                  </sourcefile>
                </package>
              </group>
            </report>
            """;

        var result = parser.Parse(Aggregate);

        var file = result.Files.Single();
        file.RawPath.Should().Be("com/example/A.java");
        file.Branches[3].Arity.Should().Be(2);
        file.Branches[3].Floor.Should().Be(1);
        file.Lines[3].Status.Should().Be(LineStatus.PartiallyCovered);
    }
}
