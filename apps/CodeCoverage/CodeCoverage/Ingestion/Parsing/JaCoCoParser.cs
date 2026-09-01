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

    public bool CanParse(string content)
    {
        if (CoberturaParser.TryGetRootName(content) != "report")
            return false;
        // Clover also roots at <coverage>, PHPUnit-crap4j at <report> too —
        // JaCoCo is recognizable by its DOCTYPE or its per-line mi/ci counters.
        return content.Contains("JACOCO", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<sessioninfo", StringComparison.Ordinal)
            || content.Contains(" mi=\"", StringComparison.Ordinal);
    }

    public ParseResult Parse(string content)
    {
        var doc = XDocument.Parse(content);
        var root = doc.Root ?? throw new InvalidDataException("Empty JaCoCo document");

        var byFile = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);

        foreach (var package in root.Elements("package"))
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

                    file.AddLine(number, coveredInstructions == 0 ? 0 : null);

                    // Same synthetic-edge model as Cobertura's (covered/total) pair.
                    var totalBranches = missedBranches + coveredBranches;
                    for (var i = 0; i < totalBranches; i++)
                    {
                        file.AddBranch(number, "0", i.ToString(), i < coveredBranches ? 1 : 0);
                    }
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
