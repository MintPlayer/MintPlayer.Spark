using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Merge semantics: MAX, never sum. A retried job, a re-run attempt, or the
/// same file uploaded twice must not inflate counts — max is idempotent under
/// all three. Branches merge per (line, block, branch) key WITHIN one report
/// format only: identity schemes differ across formats (lcov's real ids vs
/// Cobertura/JaCoCo's synthesized "0"/index edges), so a session in another
/// format contributes line status but never branch detail. Line status is
/// recomputed from merged hits + the surviving branch set.
/// </summary>
public static class CoverageMerger
{
    public static void MergeInto(FileCoverage target, ParsedFile parsed, string formatName)
    {
        var lines = target.Lines.ToDictionary(l => l.Number);
        foreach (var (number, parsedLine) in parsed.Lines)
        {
            if (lines.TryGetValue(number, out var existing))
            {
                existing.Hits = MaxNullable(existing.Hits, parsedLine.Hits);
                existing.Status = (LineStatus)Math.Max((int)existing.Status, (int)parsedLine.Status);
            }
            else
            {
                lines[number] = new LineCoverage { Number = number, Hits = parsedLine.Hits, Status = parsedLine.Status };
            }
        }

        var branches = target.Branches.ToDictionary(b => (b.Line, b.BlockId, b.BranchId));
        if (parsed.Branches.Count > 0)
        {
            if (branches.Count == 0)
            {
                target.BranchFormat = formatName;
            }
            // Pre-existing documents without a stamp adopt the first format seen.
            target.BranchFormat ??= formatName;

            if (target.BranchFormat == formatName)
            {
                foreach (var ((line, block, branch), taken) in parsed.Branches)
                {
                    if (branches.TryGetValue((line, block, branch), out var existing))
                    {
                        existing.Taken = MaxNullable(existing.Taken, taken);
                    }
                    else
                    {
                        branches[(line, block, branch)] = new BranchCoverage { Line = line, BlockId = block, BranchId = branch, Taken = taken };
                    }
                }
            }
            // else: different format — its branch identities are meaningless
            // against the existing set; the lines merged above still count.
        }

        // A line that was partial can become fully covered once another session
        // takes the remaining branches — recompute from the merged branch set.
        var partialLines = branches.Values
            .GroupBy(b => b.Line)
            .Where(g => g.Any(b => b.Taken is null or 0))
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var line in lines.Values)
        {
            var executed = line.Hits is > 0 || (line.Hits is null && line.Status != LineStatus.NotCovered);
            if (executed)
                line.Status = partialLines.Contains(line.Number) ? LineStatus.PartiallyCovered : LineStatus.Covered;
        }

        target.Lines = [.. lines.Values.OrderBy(l => l.Number)];
        target.Branches = [.. branches.Values.OrderBy(b => b.Line).ThenBy(b => b.BlockId).ThenBy(b => b.BranchId)];
    }

    /// <summary>
    /// Max-merges one stored file document into another — two builds of the
    /// same commit measuring the same file are the same situation as two
    /// sessions of one build, so the assembler reuses the session rules above.
    /// </summary>
    public static void MergeInto(FileCoverage target, FileCoverage source)
    {
        var parsed = new ParsedFile { RawPath = source.Path };
        foreach (var line in source.Lines)
            parsed.Lines[line.Number] = new ParsedLine(line.Hits, line.Status);
        foreach (var branch in source.Branches)
            parsed.Branches[(branch.Line, branch.BlockId, branch.BranchId)] = branch.Taken;

        MergeInto(target, parsed, source.BranchFormat ?? target.BranchFormat ?? "unknown");
        target.Matched |= source.Matched;
        target.BlobOid ??= source.BlobOid;
    }

    /// <summary>A deep copy with the same lines and branches — the assembler never shares list instances between documents.</summary>
    public static FileCoverage Clone(FileCoverage source)
        => new()
        {
            BuildId = source.BuildId,
            Path = source.Path,
            Matched = source.Matched,
            BranchFormat = source.BranchFormat,
            BlobOid = source.BlobOid,
            Lines = [.. source.Lines.Select(l => new LineCoverage { Number = l.Number, Hits = l.Hits, Status = l.Status })],
            Branches = [.. source.Branches.Select(b => new BranchCoverage { Line = b.Line, BlockId = b.BlockId, BranchId = b.BranchId, Taken = b.Taken })],
        };

    public static CoverageSummary Summarize(IEnumerable<FileCoverage> files)
    {
        var summary = new CoverageSummary();
        foreach (var file in files)
        {
            summary.FilesCount++;
            summary.LinesCoverable += file.Lines.Count;
            summary.LinesCovered += file.Lines.Count(l => l.Status != LineStatus.NotCovered);
            summary.BranchesTotal += file.Branches.Count;
            summary.BranchesCovered += file.Branches.Count(b => b.Taken is > 0);
        }
        return summary;
    }

    private static int? MaxNullable(int? a, int? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
