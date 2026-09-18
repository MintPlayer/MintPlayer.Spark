using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Ingestion.Parsing;

public interface ICoverageParserFactory
{
    /// <summary>Sniffs the report content and returns a matching parser, or null.</summary>
    ICoverageParser? Resolve(ReportContent content);
}

/// <summary>
/// Format detection modelled on ReportGenerator's root-element dispatch: XML
/// roots identify the XML formats, text markers (TN:/SF:) identify LCOV.
/// New formats plug in by extending the parser list.
/// </summary>
[Register(typeof(ICoverageParserFactory), ServiceLifetime.Singleton)]
public partial class CoverageParserFactory : ICoverageParserFactory
{
    /// <summary>
    /// Order matters where roots collide. Clover and Cobertura both root at
    /// &lt;coverage&gt;, so Clover is probed first and Cobertura additionally
    /// requires &lt;class — a Clover report used to be claimed by Cobertura,
    /// parse to zero files and be rejected as "noFiles" instead of as an
    /// unsupported format. Istanbul is last because it is the only JSON parser
    /// and should never be probed against an XML report.
    /// </summary>
    private static readonly ICoverageParser[] parsers =
    [
        new LcovParser(),
        new CloverParser(),
        new CoberturaParser(),
        new JaCoCoParser(),
        new IstanbulParser(),
    ];

    public ICoverageParser? Resolve(ReportContent content)
        => parsers.FirstOrDefault(p => p.CanParse(content));
}
