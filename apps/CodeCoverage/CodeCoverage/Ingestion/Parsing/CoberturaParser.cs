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

    public bool CanParse(ReportContent content)
    {
        if (TryGetRootName(content.Text) != "coverage") return false;

        // Clover roots at <coverage> too. Matching on the root name alone meant
        // a Clover report was claimed here, yielded no <class filename=> and so
        // produced zero files — reported to the user as "noFiles, format
        // cobertura" rather than as an unsupported format.
        //
        // The test is for Clover's markers rather than for Cobertura's <class,
        // because a TRUNCATED Cobertura report has no <class either and must
        // still be claimed here: it is a damaged report of a format we support,
        // which is a different diagnosis from a format we do not.
        return !LooksLikeClover(content.Text);
    }

    public ParseResult Parse(ReportContent content)
    {
        var doc = SafeXml.Load(content.Text);
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

                file.AddLine(number, (int)Math.Min(hits, int.MaxValue));

                // Prefer <conditions>, which carries per-condition identity, over
                // the aggregated condition-coverage attribute, which is a bare
                // count. gcovr and coverage.py emit the former; coverlet does not.
                var conditions = line.Element("conditions")?.Elements("condition").ToList();
                if (conditions is { Count: > 0 })
                {
                    foreach (var condition in conditions)
                    {
                        var conditionNumber = condition.Attribute("number")?.Value;
                        if (string.IsNullOrEmpty(conditionNumber)) continue;

                        // coverage="50%" — the share of this condition's own arms
                        // that were taken. Anything above 0 means it was reached.
                        var coverage = condition.Attribute("coverage")?.Value?.TrimEnd('%');
                        var taken = int.TryParse(coverage, out var percent) && percent > 0;
                        file.AddBranchArm(number, $"condition:{conditionNumber}", taken);
                    }
                    continue;
                }

                var conditionCoverage = line.Attribute("condition-coverage")?.Value;
                if (conditionCoverage is not null)
                {
                    var match = ConditionCoverageRegex().Match(conditionCoverage);
                    if (match.Success
                        && int.TryParse(match.Groups["covered"].Value, out var covered)
                        && int.TryParse(match.Groups["total"].Value, out var total))
                    {
                        // Count only: (1/2) says how many arms were taken but not
                        // which, so it contributes a floor and never an arm set.
                        file.AddBranchCount(number, covered, total);
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

    /// <summary>
    /// Clover's two structural markers under a shared &lt;coverage&gt; root: the
    /// version attribute every writer stamps, and the &lt;project&gt; element
    /// Cobertura has no equivalent of.
    /// </summary>
    internal static bool LooksLikeClover(string content)
        => content.Contains(" clover=\"", StringComparison.Ordinal)
        || content.Contains("<project", StringComparison.Ordinal);

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
