namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// Parses LCOV .info tracefiles (both lcov 1.x and 2.x record flavours).
/// Records used: SF (section start), DA (line hits), BRDA (branch), end_of_record.
/// FN/FNDA (functions) and the LF/LH/BRF/BRH summary counters are skipped —
/// totals are derived from the data instead of trusted from the file.
/// </summary>
public sealed class LcovParser : ICoverageParser
{
    public string FormatName => "lcov";

    public bool CanParse(string content)
    {
        // First non-empty line starts with TN: or SF: per the format.
        foreach (var line in EnumerateLines(content))
        {
            if (line.Length == 0) continue;
            return line.StartsWith("TN:", StringComparison.Ordinal)
                || line.StartsWith("SF:", StringComparison.Ordinal);
        }
        return false;
    }

    public ParseResult Parse(string content)
    {
        var files = new List<ParsedFile>();
        ParsedFile? current = null;

        foreach (var rawLine in EnumerateLines(content))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                current = new ParsedFile { RawPath = line[3..].Trim() };
                continue;
            }

            if (line.StartsWith("end_of_record", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    current.ResolveStatuses();
                    files.Add(current);
                }
                current = null;
                continue;
            }

            if (current is null) continue;

            if (line.StartsWith("DA:", StringComparison.Ordinal))
            {
                // DA:<line>,<count>[,<checksum>]
                var parts = line[3..].Split(',');
                if (parts.Length >= 2
                    && int.TryParse(parts[0], out var lineNumber)
                    && long.TryParse(parts[1], out var count))
                {
                    current.AddLine(lineNumber, (int)Math.Min(count, int.MaxValue));
                }
            }
            else if (line.StartsWith("BRDA:", StringComparison.Ordinal))
            {
                // BRDA:<line>,[e|f|U]<block>,<branch>,<taken>
                // <taken> is a count or '-' meaning the enclosing block never executed.
                var parts = line[5..].Split(',');
                if (parts.Length >= 4 && int.TryParse(parts[0], out var lineNumber))
                {
                    // lcov 2.x prefixes the block with e/f (exception/fallthrough)
                    // or U (unreachable) — strip any leading non-digit marker.
                    var block = parts[1].TrimStart('e', 'f', 'U');
                    var branch = parts[2];
                    int? taken = parts[3] == "-"
                        ? null
                        : long.TryParse(parts[3], out var t) ? (int)Math.Min(t, int.MaxValue) : null;
                    current.AddBranch(lineNumber, block, branch, taken);
                }
            }
        }

        // Tolerate a missing trailing end_of_record.
        if (current is not null)
        {
            current.ResolveStatuses();
            files.Add(current);
        }

        return new ParseResult { Files = files };
    }

    private static IEnumerable<string> EnumerateLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
