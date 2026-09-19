using System.Xml.Linq;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// Parses JaCoCo XML (root &lt;report&gt;). File paths are package name +
/// sourcefile name ("com/example" + "Foo.java"). JaCoCo reports instruction
/// counters per line (mi/ci = missed/covered instructions, mb/cb =
/// missed/covered branches) and carries NO execution counts — an executed
/// line's Hits stays null (the reason Hits is nullable in the model), while
/// an unexecuted one is a genuine 0.
/// </summary>
public sealed class JaCoCoParser : ICoverageParser
{
    public string FormatName => "jacoco";

    public bool CanParse(ReportContent content)
    {
        var text = content.Text;
        if (CoberturaParser.TryGetRootName(text) != "report")
            return false;
        // Clover also roots at <coverage>, PHPUnit-crap4j at <report> too —
        // JaCoCo is recognizable by its DOCTYPE or its per-line mi/ci counters.
        return text.Contains("JACOCO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<sessioninfo", StringComparison.Ordinal)
            || text.Contains(" mi=\"", StringComparison.Ordinal);
    }

    public ParseResult Parse(ReportContent content)
    {
        var doc = SafeXml.Load(content.Text);
        var root = doc.Root ?? throw new InvalidDataException("Empty JaCoCo document");

        var byFile = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);

        // Descendants, not Elements: report.dtd allows <group> to nest packages
        // (and groups within groups), which is what jacoco:report-aggregate emits
        // for a multi-module build. Direct children only parsed those to zero
        // files, so the whole upload was rejected as "noFiles".
        foreach (var package in root.Descendants("package"))
        {
            var packageName = package.Attribute("name")?.Value ?? "";

            foreach (var sourceFile in package.Elements("sourcefile"))
            {
                var fileName = sourceFile.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(fileName)) continue;

                var path = packageName.Length == 0 ? fileName : $"{packageName}/{fileName}";
                if (!byFile.TryGetValue(path, out var file))
                {
                    file = new ParsedFile { RawPath = path };
                    byFile[path] = file;
                }

                foreach (var line in sourceFile.Elements("line"))
                {
                    if (!int.TryParse(line.Attribute("nr")?.Value, out var number)) continue;
                    int.TryParse(line.Attribute("ci")?.Value, out var coveredInstructions);
                    int.TryParse(line.Attribute("mb")?.Value, out var missedBranches);
                    int.TryParse(line.Attribute("cb")?.Value, out var coveredBranches);
                    var hasMissed = int.TryParse(line.Attribute("mi")?.Value, out var missedInstructions);

                    // mi is what makes a line yellow in JaCoCo's own report:
                    // ci==0 is red, ci>0 && mi>0 is PARTIALLY covered, mi==0 is
                    // green. Ignoring it reported every instruction-partial line
                    // as fully covered.
                    file.AddLine(
                        number,
                        coveredInstructions == 0 ? 0 : null,
                        hasMissed ? missedInstructions : null);

                    // mb/cb are bare counts with no arm identity, exactly like
                    // Cobertura's condition-coverage — a floor, never an arm set.
                    if (missedBranches + coveredBranches > 0)
                        file.AddBranchCount(number, coveredBranches, missedBranches + coveredBranches);
                }
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
