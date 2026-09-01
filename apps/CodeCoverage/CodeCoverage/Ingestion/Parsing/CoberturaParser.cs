using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// Parses Cobertura XML (also emitted by coverlet, coverage.py, gcovr, PHPUnit…).
/// Several &lt;class&gt; elements share one @filename (one per type in the file),
/// so results are grouped by filename. Branch data rides in
/// condition-coverage="50% (1/2)" — the (covered/total) pair is what we read.
/// The &lt;source&gt; roots are surfaced for path normalization.
/// </summary>
public sealed partial class CoberturaParser : ICoverageParser
{
    public string FormatName => "cobertura";

    public bool CanParse(string content)
    {
        var root = TryGetRootName(content);
        return root == "coverage";
    }

    public ParseResult Parse(string content)
    {
        var doc = XDocument.Parse(content);
        var root = doc.Root ?? throw new InvalidDataException("Empty Cobertura document");

        var sources = root.Element("sources")?.Elements("source")
            .Select(s => s.Value.Trim())
            .Where(s => s.Length > 0)
            .ToArray() ?? [];

        var byFile = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);

        foreach (var cls in root.Descendants("class"))
        {
            var filename = cls.Attribute("filename")?.Value;
            if (string.IsNullOrEmpty(filename)) continue;

            if (!byFile.TryGetValue(filename, out var file))
            {
                file = new ParsedFile { RawPath = filename };
                byFile[filename] = file;
            }

            foreach (var line in cls.Elements("lines").Elements("line"))
            {
                if (!int.TryParse(line.Attribute("number")?.Value, out var number)) continue;
                long.TryParse(line.Attribute("hits")?.Value, out var hits);

                // Multiple classes for the same file describe distinct lines; a
                // duplicated line is the same run, where AddLine accumulates.
                file.AddLine(number, (int)Math.Min(hits, int.MaxValue));

                var conditionCoverage = line.Attribute("condition-coverage")?.Value;
                if (conditionCoverage is not null)
                {
                    var match = ConditionCoverageRegex().Match(conditionCoverage);
                    if (match.Success
                        && int.TryParse(match.Groups["covered"].Value, out var covered)
                        && int.TryParse(match.Groups["total"].Value, out var total))
                    {
                        // Cobertura aggregates all conditions on the line into one
                        // (covered/total) pair — model it as `total` synthetic
                        // branch edges on block "0", `covered` of them taken.
                        for (var i = 0; i < total; i++)
                        {
                            file.AddBranch(number, "0", i.ToString(), i < covered ? 1 : 0);
                        }
                    }
                }
            }
        }

        var files = byFile.Values.ToList();
        foreach (var file in files)
        {
            file.ResolveStatuses();
        }

        return new ParseResult { Files = files, SourceRoots = sources };
    }

    internal static string? TryGetRootName(string content)
    {
        try
        {
            // Cheap scan: find the first element start tag without parsing the whole doc.
            var match = RootElementRegex().Match(content);
            return match.Success ? match.Groups["name"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\(\s*(?<covered>\d+)\s*/\s*(?<total>\d+)\s*\)")]
    private static partial Regex ConditionCoverageRegex();

    [GeneratedRegex(@"<\s*(?<name>[A-Za-z][\w.-]*)[\s>]", RegexOptions.Singleline)]
    private static partial Regex RootElementRegex();
}
