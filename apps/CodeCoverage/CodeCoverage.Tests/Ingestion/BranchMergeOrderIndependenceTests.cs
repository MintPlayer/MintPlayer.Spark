using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The requirement-(1) gate: the order in which reports are uploaded must not
/// change the stored result. Asserted as a property over every permutation of a
/// report set rather than as one example, because the failure mode is a silent
/// asymmetry between two specific formats and an example test only catches the
/// pair it happens to name.
/// <para>
/// These run against the merge functions directly. They are pure, so the whole
/// permutation matrix costs nothing; a database-backed test per permutation
/// would dominate the suite's runtime for no extra coverage. The attachment-order
/// path through ParseSessionRecipient is covered once, separately.
/// </para>
/// </summary>
public class BranchMergeOrderIndependenceTests
{
    private const string Lcov = """
        TN:
        SF:src/calc.ts
        DA:2,3
        DA:5,1
        BRDA:2,0,0,2
        BRDA:2,0,1,0
        end_of_record
        """;

    private const string Cobertura = """
        <?xml version="1.0"?>
        <coverage line-rate="0.5">
          <sources><source>/work/repo</source></sources>
          <packages><package name="p"><classes>
            <class name="Calc" filename="src/calc.ts">
              <lines>
                <line number="2" hits="1" branch="true" condition-coverage="50% (1/2)"/>
                <line number="5" hits="4"/>
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private const string JaCoCo = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
        <report name="app">
          <sessioninfo id="host-1" start="1700000000000" dump="1700000001000"/>
          <package name="src">
            <sourcefile name="calc.ts">
              <line nr="2" mi="0" ci="1" mb="1" cb="1"/>
              <line nr="5" mi="0" ci="1" mb="0" cb="0"/>
            </sourcefile>
          </package>
        </report>
        """;

    private const string Clover = """
        <coverage generated="1" clover="3.2.0">
          <project timestamp="1" name="All files">
            <file name="calc.ts" path="src/calc.ts">
              <line num="2" count="1" type="cond" truecount="2" falsecount="0"/>
              <line num="5" count="2" type="stmt"/>
            </file>
          </project>
        </coverage>
        """;

    private const string Istanbul = """
        {
          "src/calc.ts": {
            "path": "src/calc.ts",
            "statementMap": {
              "0": { "start": { "line": 2, "column": 0 } },
              "1": { "start": { "line": 5, "column": 0 } }
            },
            "branchMap": {
              "0": { "type": "if", "locations": [{ "start": { "line": 2 } }, { "start": { "line": 2 } }], "line": 2 }
            },
            "s": { "0": 9, "1": 9 },
            "b": { "0": [0, 4] }
          }
        }
        """;

    private static readonly CoverageParserFactory factory = new();

    public static TheoryData<string, string[]> ReportSets => new()
    {
        { "lcov+cobertura", [Lcov, Cobertura] },
        { "lcov+istanbul", [Lcov, Istanbul] },
        { "cobertura+clover", [Cobertura, Clover] },
        { "lcov+cobertura+jacoco", [Lcov, Cobertura, JaCoCo] },
        { "all five", [Lcov, Cobertura, JaCoCo, Clover, Istanbul] },
    };

    [Theory]
    [MemberData(nameof(ReportSets))]
    public void Every_permutation_stores_the_same_document(string name, string[] reports)
    {
        var results = Permutations(reports).Select(Merge).ToList();

        results.Should().HaveCountGreaterThan(1, $"{name} should permute");

        // Every ordering of {name} must store an identical document.
        foreach (var result in results.Skip(1))
            result.Should().BeEquivalentTo(results[0]);
    }

    [Theory]
    [MemberData(nameof(ReportSets))]
    public void Every_permutation_summarizes_the_same(string name, string[] reports)
    {
        var summaries = Permutations(reports)
            .Select(order => CoverageMerger.Summarize([Merge(order)]))
            .ToList();

        summaries.Should().HaveCountGreaterThan(1, $"{name} should permute");

        // Totals for {name} must not depend on upload order either.
        foreach (var summary in summaries.Skip(1))
            summary.Should().BeEquivalentTo(summaries[0]);
    }

    [Fact]
    public void A_report_split_across_two_uploads_equals_the_same_data_in_one()
    {
        // The partition property: ParsedFile used to sum duplicate records
        // within one report while the merger maxed across reports, so splitting
        // a report changed the stored hit counts.
        const string whole = """
            TN:
            SF:src/calc.ts
            DA:1,2
            DA:2,3
            end_of_record
            """;
        const string firstHalf = """
            TN:
            SF:src/calc.ts
            DA:1,2
            end_of_record
            """;
        const string secondHalf = """
            TN:
            SF:src/calc.ts
            DA:2,3
            end_of_record
            """;

        Merge([whole]).Should().BeEquivalentTo(Merge([firstHalf, secondHalf]));
    }

    [Fact]
    public void A_count_only_report_arriving_second_still_contributes()
    {
        // The categorical loss the old model had: a foreign-format report used
        // to contribute nothing at all, not even the floor it established.
        var lcovOnly = Merge([Lcov]);
        var both = Merge([Lcov, Clover]);

        lcovOnly.Branches.Single(b => b.Line == 2).Covered.Should().Be(1);
        both.Branches.Single(b => b.Line == 2).Covered.Should().Be(2, "clover proves both arms were taken");
        both.Lines.Single(l => l.Number == 2).Status.Should().Be(LineStatus.Covered);
    }

    private static FileCoverage Merge(IEnumerable<string> reports)
    {
        var target = new FileCoverage { BuildId = "b", Path = "src/calc.ts" };
        foreach (var report in reports)
        {
            var parser = factory.Resolve(report);
            parser.Should().NotBeNull("every fixture must be recognised by exactly one parser");
            foreach (var file in parser!.Parse(report).Files)
                CoverageMerger.MergeInto(target, file);
        }
        return target;
    }

    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return [.. items];
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, index) => index != i).ToList();
            foreach (var permutation in Permutations(rest))
                yield return [items[i], .. permutation];
        }
    }
}
