using System.Text.Json;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// Parses istanbul's coverage-final.json (vitest, nyc, jest) — a flat map of
/// absolute file path to { path, statementMap, s, fnMap, f, branchMap, b }.
/// <para>
/// The richest format we ingest: branchMap names every arm of every branching
/// expression and b holds a hit count per arm, so arms keep real identity
/// ("&lt;branchMapKey&gt;:&lt;armIndex&gt;") and merge exactly across reports.
/// Functions (fnMap/f) are skipped — ParsedFile models lines and branches only,
/// the same reason lcov's FN/FNDA are skipped.
/// </para>
/// </summary>
public sealed class IstanbulParser : ICoverageParser
{
    /// <summary>
    /// Bounds the JSON the way SafeXml bounds XML. Reports are already capped
    /// upstream, but a parser that can be handed arbitrary bytes should not
    /// depend on that, and System.Text.Json has no document-size limit of its
    /// own. 256 MiB matches SafeXml.
    /// </summary>
    private const int MaxCharacters = 256 * 1024 * 1024;

    private const int MaxDepth = 64;

    public string FormatName => "istanbul";

    public bool CanParse(ReportContent content)
    {
        var text = content.Text;
        if (text.Length == 0 || text[0] != '{') return false;

        // Structural, not just "starts with {": these two keys together appear
        // in no other format we accept. Ordered last in the factory so this
        // never runs against an XML report.
        return text.Contains("\"statementMap\"", StringComparison.Ordinal)
            && text.Contains("\"branchMap\"", StringComparison.Ordinal);
    }

    public ParseResult Parse(ReportContent content)
    {
        if (content.Text.Length > MaxCharacters)
            throw new ReportTooLargeException(MaxCharacters);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.Text, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException ex)
        {
            // ClassifyParseFailure knows InvalidDataException; a raw JsonException
            // would fall through and be reported as an unhelpful generic failure.
            throw new InvalidDataException($"Malformed istanbul JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Istanbul report root is not an object.");

            var files = new List<ParsedFile>();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;

                var parsed = ParseFile(entry.Name, entry.Value);
                if (parsed is not null) files.Add(parsed);
            }

            return new ParseResult { Files = files };
        }
    }

    private static ParsedFile? ParseFile(string key, JsonElement entry)
    {
        // The outer key duplicates "path"; prefer the property and fall back.
        var rawPath = entry.TryGetProperty("path", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
            && pathElement.GetString() is { Length: > 0 } declared
                ? declared
                : key;
        if (string.IsNullOrEmpty(rawPath)) return null;

        var file = new ParsedFile { RawPath = rawPath };

        // statementMap gives each statement's location, s its execution count.
        // A statement can span lines; it is attributed to its start line, and
        // AddLine maxes when several statements share one line.
        if (entry.TryGetProperty("statementMap", out var statementMap)
            && entry.TryGetProperty("s", out var statementCounts)
            && statementMap.ValueKind == JsonValueKind.Object
            && statementCounts.ValueKind == JsonValueKind.Object)
        {
            foreach (var statement in statementMap.EnumerateObject())
            {
                if (!TryGetLine(statement.Value, out var line)) continue;
                if (!statementCounts.TryGetProperty(statement.Name, out var count)) continue;
                file.AddLine(line, ReadCount(count));
            }
        }

        if (entry.TryGetProperty("branchMap", out var branchMap)
            && entry.TryGetProperty("b", out var branchCounts)
            && branchMap.ValueKind == JsonValueKind.Object
            && branchCounts.ValueKind == JsonValueKind.Object)
        {
            foreach (var branch in branchMap.EnumerateObject())
            {
                if (!branchCounts.TryGetProperty(branch.Name, out var counts)) continue;
                if (counts.ValueKind != JsonValueKind.Array) continue;

                // Every arm is attributed to the BRANCH's line, not to its own
                // start line. Arms of one branch routinely start on different
                // lines (multi-line ternaries, binary-expr), and attributing
                // per-arm would split a single 2-arm branch across two lines and
                // render both as partial. istanbul's own lcov and clover
                // reporters attribute to the branch line; matching them is what
                // makes our istanbul and clover readings of one run agree.
                if (!TryGetBranchLine(branch.Value, out var line)) continue;

                var armIndex = 0;
                foreach (var count in counts.EnumerateArray())
                {
                    // The istanbul provider writes -1 for an arm it could not
                    // instrument. Treat it as present but untaken rather than
                    // as a hit, which is what its own reporters do.
                    var hits = ReadCount(count);
                    file.AddBranchArm(line, $"{branch.Name}:{armIndex}", hits is > 0);
                    armIndex++;
                }
            }
        }

        file.ResolveStatuses();
        return file;
    }

    private static bool TryGetBranchLine(JsonElement branch, out int line)
    {
        if (branch.TryGetProperty("line", out var declared)
            && declared.ValueKind == JsonValueKind.Number
            && declared.TryGetInt32(out line))
        {
            return true;
        }

        // Older writers omit the top-level "line" and only carry loc.
        return TryGetLine(branch, out line);
    }

    private static bool TryGetLine(JsonElement element, out int line)
    {
        line = 0;
        var location = element.TryGetProperty("loc", out var loc) ? loc : element;
        return location.ValueKind == JsonValueKind.Object
            && location.TryGetProperty("start", out var start)
            && start.ValueKind == JsonValueKind.Object
            && start.TryGetProperty("line", out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out line);
    }

    private static int ReadCount(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? (int)Math.Clamp(value, 0, int.MaxValue)
            : 0;
}
