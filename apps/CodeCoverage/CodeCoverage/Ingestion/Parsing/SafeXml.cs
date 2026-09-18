using System.Xml;
using System.Xml.Linq;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// XML reader settings for parsing coverage reports, which arrive from CI systems
/// and are therefore attacker-adjacent on any public fork PR.
///
/// <para><b>DtdProcessing.Ignore, not Prohibit.</b> Issue #417 suggests
/// <c>Prohibit</c>. Measured (M0 spike, 2026-09-18): that breaks <b>every real
/// JaCoCo report</b>, because JaCoCo writes
/// <c>&lt;!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd"&gt;</c>
/// and <c>Prohibit</c> fails the document outright with "For security reasons DTD
/// is prohibited in this XML document". <c>Ignore</c> skips the DOCTYPE without
/// declaring its entities, so JaCoCo parses and any entity reference becomes
/// "Reference to undeclared entity" — a named rejection rather than an expansion.</para>
///
/// <para><b>This closes a live defect, not a theoretical one.</b> The same spike
/// expanded a 600-byte billion-laughs document into a 500,000-character attribute
/// value through the parser as it ships today — and <c>XDocument.Load(Stream)</c>,
/// which issue #417 proposes, does <b>not</b> change that. Entity expansion is
/// unbounded until these settings are applied.</para>
/// </summary>
internal static class SafeXml
{
    /// <summary>
    /// Upper bound on characters in one report document. Ours are ~200 KB; a large
    /// monorepo's Cobertura can legitimately reach tens of megabytes, so this is set
    /// to bound memory rather than to express an expectation about report size.
    /// </summary>
    private const long MaxCharactersInDocument = 256L * 1024 * 1024;

    public static XmlReaderSettings Settings() => new()
    {
        // Skip the DOCTYPE without declaring its entities. Prohibit would reject
        // JaCoCo; Parse would reinstate billion-laughs. See the class remarks.
        DtdProcessing = DtdProcessing.Ignore,

        // No external resolution, ever: no file:// reads, no outbound requests.
        XmlResolver = null,

        // Belt and braces alongside DtdProcessing.Ignore — with no DTD parsed there
        // are no entities to expand, and this makes the intent enforceable if the
        // DtdProcessing value is ever loosened.
        MaxCharactersFromEntities = 0,

        MaxCharactersInDocument = MaxCharactersInDocument,

        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    /// <summary>
    /// Loads already-decoded report text. A TextReader-based XmlReader ignores the
    /// prolog's <c>encoding=</c> attribute, which is correct here: the decode
    /// decision was already made from the bytes in <see cref="ReportContent"/>, and
    /// re-deriving it from the declaration would risk decoding twice by two rules.
    /// </summary>
    public static XDocument Load(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), Settings());
        return XDocument.Load(reader);
    }
}
