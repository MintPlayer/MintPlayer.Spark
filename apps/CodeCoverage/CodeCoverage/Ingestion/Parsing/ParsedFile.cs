using CodeCoverage.Entities;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// Parser output for one source file mentioned in a coverage report, before
/// path normalization. Line status here reflects only this report's data;
/// merging across sessions happens later (max semantics).
/// </summary>
public sealed class ParsedFile
{
    /// <summary>Path exactly as the report states it.</summary>
    public required string RawPath { get; init; }

    /// <summary>Line number → (hits, status). Only coverable lines appear.</summary>
    public SortedDictionary<int, ParsedLine> Lines { get; } = [];

    /// <summary>
    /// Line number → branch coverage for that line. See <see cref="ParsedBranches"/>
    /// for why a line carries an arm set and a floor rather than a list of edges.
    /// </summary>
    public SortedDictionary<int, ParsedBranches> Branches { get; } = [];

    /// <summary>
    /// Records one line's execution count. MAX, not sum: a line can be reported
    /// several times within one report — Cobertura emits one &lt;class&gt; per type
    /// AND per closed generic instantiation, and those repeat the same line
    /// numbers — and summing them multiplies the count by the instantiation
    /// count. Max also matches how <see cref="CoverageMerger"/> merges across
    /// reports, which is what makes a report split across two uploads produce
    /// the same result as the same data in one upload.
    /// </summary>
    /// <param name="hits">
    /// null means "executed, count unknown" (JaCoCo carries no execution counts).
    /// It must never be demoted to 0, which means "definitely not executed".
    /// </param>
    public void AddLine(int number, int? hits)
    {
        if (Lines.TryGetValue(number, out var existing))
            Lines[number] = new ParsedLine(MaxHits(existing.Hits, hits), default);
        else
            Lines[number] = new ParsedLine(hits, default);
    }

    /// <summary>
    /// Records one branch arm that the format identifies. Only for formats that
    /// carry real arm identity — lcov's block/branch ordinals, istanbul's
    /// branchMap key plus arm index, Cobertura's &lt;condition number=&gt;.
    /// </summary>
    public void AddBranchArm(int line, string armKey, bool taken)
    {
        var branches = Branch(line);
        branches.Arms.Add(armKey);
        if (taken)
            branches.TakenArms.Add(armKey);
        branches.Arity = Math.Max(branches.Arity, branches.Arms.Count);
    }

    /// <summary>
    /// Records a line's branch coverage as a bare count, for formats that carry
    /// no arm identity — Cobertura's condition-coverage="(1/2)", JaCoCo's mb/cb,
    /// Clover's truecount/falsecount. The count becomes a FLOOR: it asserts that
    /// at least <paramref name="covered"/> arms were taken without saying which,
    /// so it can never be unioned with another report's arm set, only maxed
    /// against it.
    /// </summary>
    public void AddBranchCount(int line, int covered, int total)
    {
        var branches = Branch(line);
        branches.Floor = Math.Max(branches.Floor, covered);
        branches.Arity = Math.Max(branches.Arity, total);
    }

    private ParsedBranches Branch(int line)
    {
        if (!Branches.TryGetValue(line, out var branches))
            Branches[line] = branches = new ParsedBranches();
        return branches;
    }

    /// <summary>
    /// Derives each line's status from its hits and the branches sitting on it:
    /// unexecuted → NotCovered; executed with some branch not taken →
    /// PartiallyCovered; otherwise Covered. Call once after all records are read.
    /// </summary>
    public void ResolveStatuses()
    {
        foreach (var (number, line) in Lines.ToList())
        {
            var partial = Branches.TryGetValue(number, out var branches) && branches.IsPartial;
            var status = line.Hits switch
            {
                0 => LineStatus.NotCovered,
                _ => partial ? LineStatus.PartiallyCovered : LineStatus.Covered,
            };
            Lines[number] = line with { Status = status };
        }
    }

    /// <summary>
    /// Max where null means "unknown", not zero. null loses to any non-null:
    /// JaCoCo reports an executed line as null, and demoting that to 0 would
    /// resolve the line to NotCovered and drop it out of every percentage.
    /// </summary>
    internal static int? MaxHits(int? a, int? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

/// <summary>
/// One line's branch coverage, in the only shape that merges correctly across
/// formats: an arm SET from reports that identify arms, plus a FLOOR from
/// reports that only count them, plus the arity.
/// <para>
/// covered = max(Floor, TakenArms.Count), total = Arity. Set union and max are
/// both commutative and associative, so merging is order-independent by
/// construction rather than by test — which is the whole point. A flat edge list
/// cannot do this: Cobertura and JaCoCo synthesize positional edge ids that look
/// identical ("0"/0, "0"/1) but mean nothing, so unioning them across reports
/// would claim arms that were never covered.
/// </para>
/// </summary>
public sealed class ParsedBranches
{
    /// <summary>Every arm key seen, from identity-carrying reports only.</summary>
    public HashSet<string> Arms { get; } = [];

    /// <summary>The arm keys observed taken at least once.</summary>
    public HashSet<string> TakenArms { get; } = [];

    /// <summary>
    /// The largest "at least this many arms were taken" assertion from a
    /// count-only report. Never combined with <see cref="TakenArms"/> by
    /// addition — the two describe the same arms from different angles.
    /// </summary>
    public int Floor { get; set; }

    /// <summary>The largest arm count any report claimed for this line.</summary>
    public int Arity { get; set; }

    public int Covered => Math.Max(Floor, TakenArms.Count);

    public bool IsPartial => Covered < Arity;
}

public readonly record struct ParsedLine(int? Hits, LineStatus Status);
