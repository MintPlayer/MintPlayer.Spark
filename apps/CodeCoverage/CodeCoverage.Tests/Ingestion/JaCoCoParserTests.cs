using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;
using FluentAssertions;

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
            "com/example/Calculator.java", "com/example/Util.java", "Root.java");
    }

    [Fact]
    public void Executed_lines_have_null_hits_and_missed_lines_zero()
    {
        var result = parser.Parse(Sample);
        var file = result.Files.Single(f => f.RawPath == "com/example/Calculator.java");

        // JaCoCo has no execution counts: covered lines carry Hits = null
        // (executed, count unknown), unexecuted lines a genuine 0.
        file.Lines[10].Hits.Should().BeNull();
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
        file.Branches.Keys.Where(k => k.Line == 12).Should().HaveCount(2);
        file.Branches[(12, "0", "0")].Should().Be(1);
        file.Branches[(12, "0", "1")].Should().Be(0);
    }

    [Fact]
    public void Fully_taken_branches_stay_covered()
    {
        var result = parser.Parse(Sample);
        var file = result.Files.Single(f => f.RawPath == "com/example/Util.java");

        file.Lines[5].Status.Should().Be(LineStatus.Covered);
        file.Branches.Keys.Where(k => k.Line == 5).Should().HaveCount(2);
    }
}
