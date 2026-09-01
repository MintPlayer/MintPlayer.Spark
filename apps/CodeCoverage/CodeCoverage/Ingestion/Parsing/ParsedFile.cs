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

    /// <summary>(line, block, branch) → times taken (null = enclosing block never ran).</summary>
    public Dictionary<(int Line, string Block, string Branch), int?> Branches { get; } = [];

    public void AddLine(int number, int? hits)
    {
        if (Lines.TryGetValue(number, out var existing))
        {
            // Duplicate line records within one report (e.g. lcov appends, or
            // several Cobertura <class> elements for one file) are the same test
            // run — accumulate counts.
            Lines[number] = new ParsedLine(
                existing.Hits is null && hits is null ? null : (existing.Hits ?? 0) + (hits ?? 0),
                default);
        }
        else
        {
            Lines[number] = new ParsedLine(hits, default);
        }
    }

    public void AddBranch(int line, string block, string branch, int? taken)
    {
        var key = (line, block, branch);
        if (Branches.TryGetValue(key, out var existing))
            Branches[key] = existing is null && taken is null ? null : (existing ?? 0) + (taken ?? 0);
        else
            Branches[key] = taken;
    }

    /// <summary>
    /// Derives each line's status from its hits and the branches sitting on it:
    /// unexecuted → NotCovered; executed with some untaken branch → PartiallyCovered;
    /// otherwise Covered. Call once after all records are read.
    /// </summary>
    public void ResolveStatuses()
    {
        var partialLines = Branches
            .GroupBy(b => b.Key.Line)
            .Where(g => g.Any(b => b.Value is null or 0))
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var (number, line) in Lines.ToList())
        {
            var status = line.Hits switch
            {
                0 => LineStatus.NotCovered,
                _ => partialLines.Contains(number) ? LineStatus.PartiallyCovered : LineStatus.Covered,
            };
            Lines[number] = line with { Status = status };
        }
    }
}

public readonly record struct ParsedLine(int? Hits, LineStatus Status);
