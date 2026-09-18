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
}
