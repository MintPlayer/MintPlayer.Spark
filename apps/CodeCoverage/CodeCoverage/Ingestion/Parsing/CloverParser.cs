using System.Xml.Linq;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// Parses Clover XML (istanbul/nyc/vitest, php-code-coverage, some JS tooling).
/// <para>
/// Clover shares Cobertura's &lt;coverage&gt; root, so the two are told apart
/// structurally, never by root name: Clover nests &lt;file name=&gt; under
/// &lt;project&gt; (optionally via &lt;package&gt;) and stamps the root with
/// clover="3.2.0", while Cobertura carries &lt;class filename=&gt;.
/// </para>
/// <para>
/// Branch data is <c>&lt;line type="cond" truecount= falsecount=/&gt;</c>, which
/// despite the names is a COUNT, not a true-arm/false-arm pair: truecount is how
/// many arms on the line were taken and falsecount how many were not, aggregated
/// over every branching expression on that line. Verified against istanbul JSON
/// for the same run — a line holding two separate branches with four arms
/// between them, all taken, is reported here as truecount="4" falsecount="0",
/// which no true/false pair could express. So it maps to AddBranchCount, exactly
/// like Cobertura's condition-coverage, and carries no arm identity.
/// </para>
/// <para>
/// That holds for istanbul. <b>Atlassian Clover and Clover-PHP mean something
/// else by the same attributes</b> — how many TIMES the condition evaluated true
/// and false — where every conditional has exactly two arms, so a hot loop's
/// truecount="10000" would otherwise add 10,000 phantom branches to the
/// denominator. Both producers stamp clover="3.2.0", so they are told apart by
/// the declared &lt;metrics conditionals=&gt;; see <see cref="UsesEvaluationCounts"/>.
/// </para>
/// </summary>
public sealed class CloverParser : ICoverageParser
{
    public string FormatName => "clover";

    public bool CanParse(ReportContent content)
    {
        if (CoberturaParser.TryGetRootName(content.Text) != "coverage") return false;

        // Cheap text probe before paying for a parse — Cobertura has neither marker.
        return CoberturaParser.LooksLikeClover(content.Text);
    }

    public ParseResult Parse(ReportContent content)
    {
        var doc = SafeXml.Load(content.Text);
        var root = doc.Root ?? throw new InvalidDataException("Empty Clover document");

        var byFile = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);

        // Buffered, because which reading of truecount/falsecount is correct is a
        // property of the whole document, not of one line.
        var conditions = new List<(ParsedFile File, int Line, int True, int False)>();

        // Descendants, not Elements: <file> sits directly under <project> for
        // non-namespaced languages and under <package> for the rest.
        foreach (var fileElement in root.Descendants("file"))
        {
            // @path is absolute where present; @name is what PHPUnit fills in.
            var rawPath = fileElement.Attribute("path")?.Value is { Length: > 0 } path
                ? path
                : fileElement.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(rawPath)) continue;

            if (!byFile.TryGetValue(rawPath, out var file))
            {
                file = new ParsedFile { RawPath = rawPath };
                byFile[rawPath] = file;
            }

            foreach (var line in fileElement.Elements("line"))
            {
                if (!int.TryParse(line.Attribute("num")?.Value, out var number)) continue;
                long.TryParse(line.Attribute("count")?.Value, out var count);

                // stmt, cond and method are all executable lines. Dropping
                // method would lose every declaration line.
                file.AddLine(number, (int)Math.Min(count, int.MaxValue));

                if (line.Attribute("type")?.Value != "cond") continue;

                int.TryParse(line.Attribute("truecount")?.Value, out var taken);
                int.TryParse(line.Attribute("falsecount")?.Value, out var untaken);
                if (taken + untaken > 0)
                    conditions.Add((file, number, taken, untaken));
            }
        }

        var evaluationCounts = UsesEvaluationCounts(root, conditions);
        foreach (var (file, number, taken, untaken) in conditions)
        {
            if (evaluationCounts)
            {
                // Atlassian semantics: one conditional, two arms, and the counts
                // say how many TIMES each side evaluated. A hot loop's
                // truecount="10000" is one taken arm, not 10,000 of them.
                var covered = (taken > 0 ? 1 : 0) + (untaken > 0 ? 1 : 0);
                file.AddBranchCount(number, covered, 2);
            }
            else
            {
                file.AddBranchCount(number, taken, taken + untaken);
            }
        }

        var files = byFile.Values.ToList();
        foreach (var file in files)
        {
            file.ResolveStatuses();
        }

        return new ParseResult { Files = files };
    }

    /// <summary>
    /// Decides which of the two incompatible readings of truecount/falsecount the
    /// document uses. Both producers stamp clover="3.2.0", so the root name and
    /// version cannot tell them apart — but the declared
    /// <c>&lt;metrics conditionals=&gt;</c> can, because the two compute it
    /// differently:
    /// <list type="bullet">
    /// <item>istanbul/nyc/vitest — truecount/falsecount are ARM counts, so
    /// conditionals is their sum over every cond line.</item>
    /// <item>Atlassian Clover / Clover-PHP — they are EVALUATION counts, every
    /// conditional has exactly two arms, so conditionals is 2 per cond line.</item>
    /// </list>
    /// Only an unambiguous match flips the reading: when the declared total is
    /// missing, or agrees with both formulas, we keep the istanbul reading, which
    /// is the one measured against real istanbul JSON in #420 and the only one we
    /// knowingly ingest.
    /// </summary>
    private static bool UsesEvaluationCounts(
        XElement root,
        List<(ParsedFile File, int Line, int True, int False)> conditions)
    {
        if (conditions.Count == 0) return false;

        var declared = root.Descendants("metrics")
            .Select(m => m.Attribute("conditionals")?.Value)
            .Where(v => v is not null)
            .Select(v => int.TryParse(v, out var n) ? n : (int?)null)
            .FirstOrDefault(n => n is not null);
        if (declared is null) return false;

        var asArmCounts = conditions.Sum(c => c.True + c.False);
        var asEvaluations = conditions.Count * 2;

        return declared == asEvaluations && declared != asArmCounts;
    }
}
