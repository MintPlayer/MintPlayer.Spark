using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

public class IstanbulParserTests
{
    private readonly IstanbulParser parser = new();

    // Distilled from real vitest/v8 output. Branch 0 is a cond-expr whose second
    // arm was never taken; branches 6 and 7 both sit on line 99 (an `if` and a
    // `binary-expr`), which is the shape that makes clover report 4 arms there.
    private const string Sample = """
        {
          "/work/repo/src/capabilities.ts": {
            "path": "/work/repo/src/capabilities.ts",
            "statementMap": {
              "0": { "start": { "line": 29, "column": 2 }, "end": { "line": 31, "column": 3 } },
              "1": { "start": { "line": 49, "column": 20 }, "end": { "line": 49, "column": 61 } },
              "2": { "start": { "line": 99, "column": 4 }, "end": { "line": 99, "column": 40 } },
              "3": { "start": { "line": 120, "column": 2 }, "end": { "line": 120, "column": 9 } }
            },
            "fnMap": {
              "0": { "name": "fetchCapabilities", "decl": { "start": { "line": 12, "column": 9 } }, "line": 12 }
            },
            "branchMap": {
              "0": {
                "loc": { "start": { "line": 49, "column": 20 } },
                "type": "cond-expr",
                "locations": [
                  { "start": { "line": 49, "column": 45 } },
                  { "start": { "line": 50, "column": 61 } }
                ],
                "line": 49
              },
              "6": {
                "loc": { "start": { "line": 99, "column": 4 } },
                "type": "if",
                "locations": [{ "start": { "line": 99, "column": 4 } }, { "start": { "line": 101, "column": 4 } }],
                "line": 99
              },
              "7": {
                "loc": { "start": { "line": 99, "column": 8 } },
                "type": "binary-expr",
                "locations": [{ "start": { "line": 99, "column": 8 } }, { "start": { "line": 99, "column": 24 } }],
                "line": 99
              }
            },
            "s": { "0": 4, "1": 1, "2": 6, "3": 0 },
            "f": { "0": 3 },
            "b": { "0": [1, 0], "6": [1, 5], "7": [6, 5] }
          }
        }
        """;

    [Fact]
    public void CanParse_recognises_istanbul_and_rejects_the_other_formats()
    {
        parser.CanParse(Sample).Should().BeTrue();
        parser.CanParse("""<coverage clover="3.2.0"><project/></coverage>""").Should().BeFalse();
        parser.CanParse("""<coverage><packages/></coverage>""").Should().BeFalse();
        parser.CanParse("""<report name="jacoco"/>""").Should().BeFalse();
        parser.CanParse("TN:\nSF:x").Should().BeFalse();
        parser.CanParse("""{"not":"coverage"}""").Should().BeFalse();
    }

    [Fact]
    public void Statements_become_lines_attributed_to_their_start_line()
    {
        var file = parser.Parse(Sample).Files.Single();

        file.RawPath.Should().Be("/work/repo/src/capabilities.ts");
        file.Lines.Keys.Should().BeEquivalentTo([29, 49, 99, 120]);
        file.Lines[29].Hits.Should().Be(4);
        file.Lines[120].Hits.Should().Be(0);
        file.Lines[120].Status.Should().Be(LineStatus.NotCovered);
    }

    [Fact]
    public void Branch_arms_keep_real_identity()
    {
        var file = parser.Parse(Sample).Files.Single();

        file.Branches[49].Arity.Should().Be(2);
        file.Branches[49].TakenArms.Should().BeEquivalentTo(["0:0"]);
        file.Branches[49].Floor.Should().Be(0, "an identity-carrying format never sets a floor");
        file.Lines[49].Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Several_branches_on_one_line_accumulate_into_that_line()
    {
        var file = parser.Parse(Sample).Files.Single();

        // Branches 6 and 7 both sit on line 99: four arms, all taken. This is
        // exactly what clover reports as truecount="4" falsecount="0".
        file.Branches[99].Arity.Should().Be(4);
        file.Branches[99].TakenArms.Should().BeEquivalentTo(["6:0", "6:1", "7:0", "7:1"]);
        file.Lines[99].Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void Arms_are_attributed_to_the_branch_line_not_their_own()
    {
        var file = parser.Parse(Sample).Files.Single();

        // Branch 0's second arm starts on line 50, but both arms belong to the
        // branch's line. Attributing per arm would make lines 49 and 50 each
        // look like a separate one-armed partial branch.
        file.Branches.Should().NotContainKey(50);
        file.Branches[49].Arity.Should().Be(2);
    }

    [Fact]
    public void Negative_arm_counts_are_untaken_rather_than_hits()
    {
        // The istanbul provider writes -1 for an arm it could not instrument.
        const string negative = """
            {
              "/a.ts": {
                "path": "/a.ts",
                "statementMap": { "0": { "start": { "line": 1, "column": 0 } } },
                "branchMap": { "0": { "type": "if", "locations": [{ "start": { "line": 1 } }, { "start": { "line": 1 } }], "line": 1 } },
                "s": { "0": 1 },
                "b": { "0": [-1, 2] }
              }
            }
            """;

        var file = parser.Parse(negative).Files.Single();
        file.Branches[1].TakenArms.Should().BeEquivalentTo(["0:1"]);
        file.Lines[1].Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void Malformed_json_is_reported_as_invalid_rather_than_escaping()
    {
        // ClassifyParseFailure knows InvalidDataException; a raw JsonException
        // would be reported as an unhelpful generic failure.
        var act = () => parser.Parse("""{"a.ts": {"statementMap": {"branchMap": }}""");
        act.Should().Throw<InvalidDataException>();
    }
}
