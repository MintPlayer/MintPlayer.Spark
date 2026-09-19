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

    /// <summary>
    /// Branch lines the parser could only read by a degraded, last-resort rule —
    /// today just Cobertura's <c>&lt;conditions&gt;</c> without a usable
    /// <c>condition-coverage</c>, where element count stands in for an arity the
    /// document never states.
    /// <para>
    /// Surfaced rather than silent because that last resort is the shape of #423
    /// scoped to a case no measured producer exhibits — and #423 itself came from
    /// an unmeasured assumption about producer behaviour. A non-zero count here
    /// means a real producer does exhibit it, and the rule needs measuring against
    /// that producer instead of reasoning.
    /// </para>
    /// </summary>
    public int DegradedBranchLines { get; init; }
}
