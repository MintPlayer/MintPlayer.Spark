using System.Text.RegularExpressions;
using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Regression cover for #423. Cobertura is a COUNT-ONLY format: a &lt;condition&gt;
/// element names a branch POINT, not one arm of one, so reading those elements as
/// arms halved every arity and hid partial branches.
///
/// Every XML excerpt below is verbatim coverlet output, and the two fixture files
/// are real tool output. That is deliberate: #420 shipped this defect past a green
/// suite because the only Cobertura sample in the repo was hand-written and simply
/// omitted the &lt;conditions&gt; element the parser had just learned to read.
/// </summary>
public class CoberturaConditionsTests
{
    private readonly CoberturaParser parser = new();

    private static string Wrap(string lines) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0" branch-rate="0" version="1.9" timestamp="0">
          <sources><source>/repo</source></sources>
          <packages><package name="P"><classes>
            <class name="C" filename="src/C.cs"><lines>
        {lines}
            </lines></class>
          </classes></package></packages>
        </coverage>
        """;

    // Verbatim from a coverlet report: one aggregate <condition> for a two-armed
    // jump, with the true split living only in condition-coverage.
    private const string PartialJump = """
            <line number="97" hits="1" branch="True" condition-coverage="50% (1/2)">
              <conditions><condition number="70" type="jump" coverage="50%" /></conditions>
            </line>
        """;

    private const string FullJump = """
            <line number="87" hits="1" branch="True" condition-coverage="100% (2/2)">
              <conditions><condition number="78" type="jump" coverage="100%" /></conditions>
            </line>
        """;

    private const string UntakenJump = """
            <line number="209" hits="0" branch="True" condition-coverage="0% (0/2)">
              <conditions><condition number="62" type="jump" coverage="0%" /></conditions>
            </line>
        """;

    // A 19-way switch reported through a single <condition>. Under the #423 defect
    // this was stored as 1/1 — a fully covered switch on 5 untaken arms.
    private const string PartialSwitch = """
            <line number="134" hits="9" branch="True" condition-coverage="73.68% (14/19)">
              <conditions><condition number="238" type="switch" coverage="73.68%" /></conditions>
            </line>
        """;

    // Two branch points on one line: two <condition> elements, four arms.
    private const string TwoPointsOneLine = """
            <line number="178" hits="1" branch="True" condition-coverage="50% (2/4)">
              <conditions>
                <condition number="203" type="jump" coverage="50%" />
                <condition number="240" type="jump" coverage="50%" />
              </conditions>
            </line>
        """;

    [Fact]
    public void An_aggregate_condition_element_does_not_hide_a_partial_branch()
    {
        var file = parser.Parse(Wrap(PartialJump)).Files.Single();

        file.Branches[97].Arity.Should().Be(2);
        file.Branches[97].Floor.Should().Be(1);
        file.Branches[97].Covered.Should().Be(1);
        file.Branches[97].IsPartial.Should().BeTrue();
        file.Lines[97].Status.Should().Be(LineStatus.PartiallyCovered);
    }

    [Fact]
    public void A_fully_taken_two_arm_branch_keeps_its_real_arity()
    {
        var file = parser.Parse(Wrap(FullJump)).Files.Single();

        // The defect stored this as 1/1 — right status, wrong denominator, so the
        // branch rate drifted on covered lines too, not only on partial ones.
        file.Branches[87].Arity.Should().Be(2);
        file.Branches[87].Covered.Should().Be(2);
        file.Branches[87].IsPartial.Should().BeFalse();
        file.Lines[87].Status.Should().Be(LineStatus.Covered);
    }

    [Fact]
    public void An_untaken_branch_reports_both_arms_missed()
    {
        var file = parser.Parse(Wrap(UntakenJump)).Files.Single();

        file.Branches[209].Arity.Should().Be(2);
        file.Branches[209].Covered.Should().Be(0);
        file.Lines[209].Status.Should().Be(LineStatus.NotCovered);
    }

    [Fact]
    public void A_switch_condition_keeps_its_non_binary_arity()
    {
        var file = parser.Parse(Wrap(PartialSwitch)).Files.Single();

        // Why "arity = 2 x conditions" would also have been wrong.
        file.Branches[134].Arity.Should().Be(19);
        file.Branches[134].Floor.Should().Be(14);
        file.Branches[134].IsPartial.Should().BeTrue();
    }

    [Fact]
    public void Two_branch_points_on_one_line_sum_their_arms()
    {
        var file = parser.Parse(Wrap(TwoPointsOneLine)).Files.Single();

        file.Branches[178].Arity.Should().Be(4);
        file.Branches[178].Covered.Should().Be(2);
        file.Branches[178].IsPartial.Should().BeTrue();
    }

    [Fact]
    public void Cobertura_never_claims_arm_identity()
    {
        // `condition:70` names a branch point, so two reports that each take a
        // different arm of it would both report the same key and the arm set could
        // never grow. Cobertura must therefore contribute a floor and never an arm.
        var result = parser.Parse(Wrap(
            PartialJump + FullJump + UntakenJump + PartialSwitch + TwoPointsOneLine));

        foreach (var branches in result.Files.Single().Branches.Values)
            branches.TakenArms.Should().BeEmpty();
    }

    [Fact]
    public void The_conditions_element_makes_no_difference_to_the_result()
    {
        // The one-step reproduction from #423: deleting <conditions> used to flip a
        // line from covered to partial. The two readings must now agree exactly.
        var withConditions = Wrap(
            PartialJump + FullJump + UntakenJump + PartialSwitch + TwoPointsOneLine);
        var without = Regex.Replace(
            withConditions, @"<conditions>.*?</conditions>", string.Empty, RegexOptions.Singleline);

        var a = parser.Parse(withConditions).Files.Single();
        var b = parser.Parse(without).Files.Single();

        b.Branches.Keys.Should().BeEquivalentTo(a.Branches.Keys);
        foreach (var (line, branches) in a.Branches)
        {
            branches.Arity.Should().Be(b.Branches[line].Arity);
            branches.Floor.Should().Be(b.Branches[line].Floor);
            branches.Covered.Should().Be(b.Branches[line].Covered);
            branches.IsPartial.Should().Be(b.Branches[line].IsPartial);
            a.Lines[line].Status.Should().Be(b.Lines[line].Status);
        }
    }

    [Fact]
    public void Real_coverlet_output_parses_to_its_documented_branch_totals()
    {
        var file = parser.Parse(Fixture.Read("coverlet.cobertura.xml")).Files.Single();

        var branchLines = file.Branches.Count;
        var covered = file.Branches.Values.Sum(b => b.Covered);
        var total = file.Branches.Values.Sum(b => b.Arity);
        var partial = file.Lines.Values.Count(l => l.Status == LineStatus.PartiallyCovered);

        // Counted independently from the report's own condition-coverage pairs.
        // Before the fix this class read 36/50 as 21/21.
        branchLines.Should().Be(21);
        covered.Should().Be(36);
        total.Should().Be(50);

        // 9 of the 21 branch lines have an untaken arm, but two of those were
        // never executed at all (hits="0" condition-coverage="0% (0/2)"), and an
        // unexecuted line is NotCovered rather than partial — status comes from
        // hits first, branches second.
        partial.Should().Be(7);
        file.Lines[127].Status.Should().Be(LineStatus.NotCovered);
        file.Lines[130].Status.Should().Be(LineStatus.NotCovered);
        file.Branches[127].IsPartial.Should().BeTrue();
    }

    [Fact]
    public void Real_coverage_py_output_parses_although_it_emits_no_conditions()
    {
        // coverage.py is the third Cobertura producer and emits no <conditions> at
        // all, so it always took the count path and was never affected by #423.
        // Pinned so the count path cannot be removed as "the fallback".
        var file = parser.Parse(Fixture.Read("coveragepy.cobertura.xml")).Files.Single();

        file.Branches.Count.Should().Be(5);
        file.Branches.Values.Sum(b => b.Covered).Should().Be(4);
        file.Branches.Values.Sum(b => b.Arity).Should().Be(10);
        file.Branches.Values.Count(b => b.IsPartial).Should().Be(4);
    }

    [Fact]
    public void The_last_resort_reading_reports_itself()
    {
        // <conditions> with no usable condition-coverage is the one path still
        // counting elements as points of unknown arity — the shape of #423,
        // narrowed to a case no measured producer exhibits. If a producer does
        // exhibit it, that must be visible rather than silently under-stated.
        var result = parser.Parse(Wrap("""
                <line number="5" hits="3" branch="true">
                  <conditions><condition number="70" type="jump" coverage="50%" /></conditions>
                </line>
            """));

        result.DegradedBranchLines.Should().Be(1);
        result.Files.Single().Branches[5].Arity.Should().Be(1);
    }

    [Fact]
    public void A_normally_read_report_is_not_flagged_as_degraded()
    {
        var result = parser.Parse(Wrap(PartialJump + FullJump + PartialSwitch));

        result.DegradedBranchLines.Should().Be(0);
    }

    [Fact]
    public void A_gcovr_shaped_line_reads_its_arity_from_condition_coverage()
    {
        // gcovr emits exactly one <condition number="0" type="jump" coverage="X%"/>
        // per line, aggregating the whole line's stat — the same shape as coverlet,
        // despite #420 classifying gcovr as arm-identifying. Shape taken from
        // gcovr/formats/cobertura/write.py::_condition_element (gcovr 8.6).
        var file = parser.Parse(Wrap("""
                <line number="5" hits="3" branch="true" condition-coverage="50% (1/2)">
                  <conditions><condition number="0" type="jump" coverage="50%" /></conditions>
                </line>
            """)).Files.Single();

        file.Branches[5].Arity.Should().Be(2);
        file.Branches[5].Covered.Should().Be(1);
        file.Lines[5].Status.Should().Be(LineStatus.PartiallyCovered);
    }
}
