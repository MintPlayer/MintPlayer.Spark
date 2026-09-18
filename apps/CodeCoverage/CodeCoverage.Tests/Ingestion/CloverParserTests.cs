using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

public class CloverParserTests
{
    private readonly CloverParser parser = new();

    // Distilled from real istanbul/vitest output: <file> directly under
    // <project>, absolute @path alongside a relative @name, and cond lines whose
    // truecount/falsecount are taken/untaken ARM COUNTS.
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <coverage generated="1789767653871" clover="3.2.0">
          <project timestamp="1789767653871" name="All files">
            <metrics statements="5" coveredstatements="4" conditionals="6" coveredconditionals="5"/>
            <file name="capabilities.ts" path="/work/repo/src/capabilities.ts">
              <metrics statements="5" coveredstatements="4"/>
              <line num="29" count="4" type="stmt"/>
              <line num="49" count="1" type="cond" truecount="1" falsecount="1"/>
              <line num="55" count="8" type="cond" truecount="2" falsecount="0"/>
              <line num="99" count="6" type="cond" truecount="4" falsecount="0"/>
              <line num="120" count="0" type="stmt"/>
              <line num="12" count="3" type="method" name="fetchCapabilities"/>
            </file>
          </project>
        </coverage>
        """;

    [Fact]
    public void CanParse_recognises_clover_and_rejects_the_other_formats()
    {
        parser.CanParse(Sample).Should().BeTrue();
        parser.CanParse("""<report name="jacoco"/>""").Should().BeFalse();
        parser.CanParse("TN:\nSF:x").Should().BeFalse();
        parser.CanParse("""{"a.ts":{"statementMap":{},"branchMap":{}}}""").Should().BeFalse();
    }

    [Fact]
    public void CanParse_rejects_a_cobertura_report_despite_the_shared_root()
    {
        const string cobertura = """
            <?xml version="1.0"?>
            <coverage line-rate="0.5">
              <packages><package name="p"><classes>
                <class name="C" filename="src/C.cs"><lines><line number="1" hits="1"/></lines></class>
              </classes></package></packages>
            </coverage>
            """;

        parser.CanParse(cobertura).Should().BeFalse();
    }

    [Fact]
    public void Uses_the_absolute_path_and_reads_every_executable_line_type()
    {
        var file = parser.Parse(Sample).Files.Single();

        file.RawPath.Should().Be("/work/repo/src/capabilities.ts");
        // stmt, cond and method are all executable; dropping method would lose
        // every declaration line.
        file.Lines.Keys.Should().BeEquivalentTo([12, 29, 49, 55, 99, 120]);
        file.Lines[29].Hits.Should().Be(4);
        file.Lines[120].Status.Should().Be(LineStatus.NotCovered);
    }

    [Fact]
    public void Truecount_and_falsecount_are_arm_counts_not_arm_identity()
    {
        var file = parser.Parse(Sample).Files.Single();

        // Line 49: one of two arms taken → partial, floor 1.
        file.Branches[49].Arity.Should().Be(2);
        file.Branches[49].Floor.Should().Be(1);
        file.Branches[49].TakenArms.Should().BeEmpty("clover names no arms");
        file.Lines[49].Status.Should().Be(LineStatus.PartiallyCovered);

        // Line 99 carries FOUR arms across two branching expressions, all taken.
        // No true/false pair could express that — this is what proves the
        // attributes are counts rather than a two-arm identity.
        file.Branches[99].Arity.Should().Be(4);
        file.Branches[99].Floor.Should().Be(4);
        file.Lines[99].Status.Should().Be(LineStatus.Covered);

        file.Branches[55].Arity.Should().Be(2);
        file.Lines[55].Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void Reads_files_nested_under_a_package()
    {
        const string packaged = """
            <coverage generated="1" clover="3.2.0">
              <project timestamp="1" name="All files">
                <package name="app">
                  <file name="src/a.php" path="/work/src/a.php">
                    <line num="3" count="7" type="stmt"/>
                  </file>
                </package>
              </project>
            </coverage>
            """;

        var file = parser.Parse(packaged).Files.Single();
        file.RawPath.Should().Be("/work/src/a.php");
        file.Lines[3].Hits.Should().Be(7);
    }

    [Fact]
    public void Falls_back_to_name_when_no_path_attribute_is_present()
    {
        const string nameOnly = """
            <coverage generated="1" clover="3.2.0">
              <project timestamp="1" name="All files">
                <file name="/abs/src/a.php">
                  <line num="1" count="1" type="stmt"/>
                </file>
              </project>
            </coverage>
            """;

        parser.Parse(nameOnly).Files.Single().RawPath.Should().Be("/abs/src/a.php");
    }
}
