using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

public class CoberturaParserTests
{
    private readonly CoberturaParser parser = new();

    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.75" branch-rate="0.5" version="1.9" timestamp="1700000000">
          <sources>
            <source>/home/runner/work/repo/repo</source>
          </sources>
          <packages>
            <package name="MyApp" line-rate="0.75" branch-rate="0.5" complexity="2">
              <classes>
                <class name="MyApp.Calculator" filename="src/Calculator.cs" line-rate="0.75" branch-rate="0.5">
                  <methods>
                    <method name="Add" signature="(int,int)" line-rate="1" branch-rate="1">
                      <lines>
                        <line number="10" hits="4" branch="false" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="10" hits="4" branch="false" />
                    <line number="12" hits="2" branch="true" condition-coverage="50% (1/2)" />
                    <line number="14" hits="0" branch="false" />
                  </lines>
                </class>
                <class name="MyApp.Calculator+Nested" filename="src/Calculator.cs" line-rate="1" branch-rate="1">
                  <lines>
                    <line number="20" hits="1" branch="false" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    [Fact]
    public void CanParse_by_root_element()
    {
        parser.CanParse(Sample).Should().BeTrue();
        parser.CanParse("<report name=\"jacoco\"/>").Should().BeFalse();
        parser.CanParse("TN:\nSF:x").Should().BeFalse();
    }

    [Fact]
    public void Groups_multiple_classes_by_filename_and_reads_source_roots()
    {
        var result = parser.Parse(Sample);

        result.SourceRoots.Should().ContainSingle().Which.Should().Be("/home/runner/work/repo/repo");
        result.Files.Should().ContainSingle();

        var file = result.Files.Single();
        file.RawPath.Should().Be("src/Calculator.cs");
        // Lines from both classes of the file, methods section not double-counted
        // into a separate file. Line 10 appears in <methods> and <lines> of the
        // same class — accumulated (same run, 4+4).
        file.Lines.Keys.Should().BeEquivalentTo([10, 12, 14, 20]);
    }

    [Fact]
    public void Condition_coverage_becomes_partial_status_and_branches()
    {
        var file = parser.Parse(Sample).Files.Single();

        file.Lines[12].Status.Should().Be(LineStatus.PartiallyCovered);
        file.Branches[(12, "0", "0")].Should().Be(1);
        file.Branches[(12, "0", "1")].Should().Be(0);

        file.Lines[14].Status.Should().Be(LineStatus.NotCovered);
        file.Lines[20].Status.Should().Be(LineStatus.Covered);
    }
}
