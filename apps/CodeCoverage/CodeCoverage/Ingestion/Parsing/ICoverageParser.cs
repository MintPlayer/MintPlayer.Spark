namespace CodeCoverage.Ingestion.Parsing;

public interface ICoverageParser
{
    string FormatName { get; }

    /// <summary>Cheap sniff on the (decompressed) report content.</summary>
    bool CanParse(string content);

    ParseResult Parse(string content);
}

public sealed class ParseResult
{
    public required IReadOnlyList<ParsedFile> Files { get; init; }

    /// <summary>Source roots declared by the report (Cobertura &lt;source&gt;), for path resolution.</summary>
    public string[] SourceRoots { get; init; } = [];
}
