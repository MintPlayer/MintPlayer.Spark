using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Ingestion.Parsing;

public interface ICoverageParserFactory
{
    /// <summary>Sniffs the report content and returns a matching parser, or null.</summary>
    ICoverageParser? Resolve(string content);
}

/// <summary>
/// Format detection modelled on ReportGenerator's root-element dispatch: XML
/// roots identify the XML formats, text markers (TN:/SF:) identify LCOV.
/// New formats plug in by extending the parser list.
/// </summary>
[Register(typeof(ICoverageParserFactory), ServiceLifetime.Singleton)]
public partial class CoverageParserFactory : ICoverageParserFactory
{
    private static readonly ICoverageParser[] parsers =
    [
        new LcovParser(),
        new CoberturaParser(),
        new JaCoCoParser(),
    ];

    public ICoverageParser? Resolve(string content)
        => parsers.FirstOrDefault(p => p.CanParse(content));
}
