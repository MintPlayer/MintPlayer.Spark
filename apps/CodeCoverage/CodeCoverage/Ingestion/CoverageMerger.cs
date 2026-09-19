using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Merge semantics: MAX and UNION, never sum. A retried job, a re-run attempt,
/// or the same file uploaded twice must not inflate counts — both operations are
/// idempotent under all three.
/// <para>
/// Branches merge across report formats, not within one. Each line keeps an arm
/// SET from formats that identify arms and a FLOOR from formats that only count
/// them (see <see cref="LineBranchCoverage"/>), and merging unions the sets and
/// maxes the floors and arities. Because union and max are commutative and
/// associative, <b>the stored result does not depend on upload order</b> — the
/// property the old per-format guard violated by keeping whichever format
/// arrived first and discarding the rest.
/// </para>
/// <para>Line status is recomputed from merged hits plus the merged branch set.</para>
/// </summary>
public static class CoverageMerger
{
    public static void MergeInto(FileCoverage target, ParsedFile parsed)
    {
        var lines = target.Lines.ToDictionary(l => l.Number);
        foreach (var (number, parsedLine) in parsed.Lines)
        {
            if (lines.TryGetValue(number, out var existing))
            {
                existing.Hits = ParsedFile.MaxHits(existing.Hits, parsedLine.Hits);
                existing.Status = (LineStatus)Math.Max((int)existing.Status, (int)parsedLine.Status);
                existing.InstructionsMissed = ParsedFile.MinMissed(
                    existing.InstructionsMissed, parsedLine.InstructionsMissed);
            }
            else
            {
                lines[number] = new LineCoverage
                {
                    Number = number,
                    Hits = parsedLine.Hits,
                    Status = parsedLine.Status,
                    InstructionsMissed = parsedLine.InstructionsMissed,
                };
            }
        }

        var branches = target.Branches.ToDictionary(b => b.Line);
        foreach (var (line, parsedBranches) in parsed.Branches)
        {
            if (!branches.TryGetValue(line, out var existing))
                branches[line] = existing = new LineBranchCoverage { Line = line };

            existing.Arity = Math.Max(existing.Arity, parsedBranches.Arity);
            existing.Floor = Math.Max(existing.Floor, parsedBranches.Floor);
            existing.TakenArms = [.. existing.TakenArms.Union(parsedBranches.TakenArms, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        // A line that was partial can become fully covered once another report
        // takes the remaining arms — recompute from the merged branch set.
        foreach (var line in lines.Values)
        {
            var executed = line.Hits is > 0 || (line.Hits is null && line.Status != LineStatus.NotCovered);
            if (executed)
            {
                var partial =
                    (branches.TryGetValue(line.Number, out var lineBranches) && lineBranches.IsPartial)
                    || line.InstructionsMissed > 0;

                line.Status = partial ? LineStatus.PartiallyCovered : LineStatus.Covered;
            }
        }

        target.Lines = [.. lines.Values.OrderBy(l => l.Number)];
        target.Branches = [.. branches.Values.OrderBy(b => b.Line)];
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
            parsed.Lines[line.Number] = new ParsedLine(line.Hits, line.Status, line.InstructionsMissed);
        foreach (var branch in source.Branches)
        {
            var parsedBranches = new ParsedBranches { Arity = branch.Arity, Floor = branch.Floor };
            foreach (var arm in branch.TakenArms)
            {
                parsedBranches.Arms.Add(arm);
                parsedBranches.TakenArms.Add(arm);
            }
            parsed.Branches[branch.Line] = parsedBranches;
        }

        MergeInto(target, parsed);
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
            BlobOid = source.BlobOid,
            Lines = [.. source.Lines.Select(l => new LineCoverage
            {
                Number = l.Number,
                Hits = l.Hits,
                Status = l.Status,
                InstructionsMissed = l.InstructionsMissed,
            })],
            Branches = [.. source.Branches.Select(b => new LineBranchCoverage
            {
                Line = b.Line,
                Arity = b.Arity,
                Floor = b.Floor,
                TakenArms = [.. b.TakenArms],
            })],
        };

    public static CoverageSummary Summarize(IEnumerable<FileCoverage> files)
    {
        var summary = new CoverageSummary();
        foreach (var file in files)
        {
            summary.FilesCount++;
            summary.LinesCoverable += file.Lines.Count;
            summary.LinesCovered += file.Lines.Count(l => l.Status != LineStatus.NotCovered);
            summary.BranchesTotal += file.Branches.Sum(b => b.Arity);
            summary.BranchesCovered += file.Branches.Sum(b => b.Covered);
        }
        return summary;
    }
}
