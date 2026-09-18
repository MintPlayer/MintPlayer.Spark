namespace CodeCoverage.Ingestion.Parsing;

public interface ICoverageParser
{
    string FormatName { get; }

    /// <summary>Cheap sniff on the normalised report content.</summary>
    bool CanParse(ReportContent content);

    ParseResult Parse(ReportContent content);
}

public sealed class ParseResult
{
    public required IReadOnlyList<ParsedFile> Files { get; init; }

    /// <summary>Source roots declared by the report (Cobertura &lt;source&gt;), for path resolution.</summary>
    public string[] SourceRoots { get; init; } = [];
}
