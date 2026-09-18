using System.Text;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// M0 SPIKE part 2 — THROWAWAY. Two questions the first spike raised:
/// does an external entity in ELEMENT content actually resolve (the real XXE
/// shape), and which XmlReaderSettings keep real JaCoCo (which ships a DOCTYPE)
/// parsing while closing entity expansion?
/// </summary>
public class IngestByteFidelitySpike2
{
    private static readonly string OutputPath =
        Path.Combine(Path.GetTempPath(), "m0-spike2.txt");

    // The real XXE shape: entity expanded into element content, not an attribute.
    private const string XxeInText = """
        <?xml version="1.0"?>
        <!DOCTYPE coverage [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
        <coverage line-rate="1" version="1.9">
          <sources><source>&xxe;</source></sources>
          <packages><package name="P"><classes>
            <class name="C" filename="src/a.cs"><lines><line number="1" hits="1"/></lines></class>
          </classes></package></packages>
        </coverage>
        """;

    private const string JaCoCoWithDoctype = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
        <report name="demo">
          <package name="com/example">
            <sourcefile name="Foo.java"><line nr="3" mi="0" ci="4" mb="0" cb="0"/></sourcefile>
          </package>
        </report>
        """;

    private const string BillionLaughs = """
        <?xml version="1.0"?>
        <!DOCTYPE coverage [
          <!ENTITY a "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa">
          <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
          <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
          <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
          <!ENTITY e "&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;">
        ]>
        <coverage line-rate="1" version="1.9">
          <sources><source>&e;</source></sources>
          <packages><package name="P"><classes>
            <class name="C" filename="src/a.cs"><lines><line number="1" hits="1"/></lines></class>
          </classes></package></packages>
        </coverage>
        """;

    private static string Describe(Func<XDocument> load)
    {
        try
        {
            var doc = load();
            var source = doc.Root?.Element("sources")?.Element("source")?.Value ?? "(no <source>)";
            var shown = source.Length > 60 ? $"{source.Length} chars: {source[..60]}…" : $"'{source}'";
            return $"OK — root <{doc.Root?.Name.LocalName}>, source = {shown}";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static XDocument LoadWith(string xml, XmlReaderSettings settings)
    {
        using var reader = XmlReader.Create(new MemoryStream(Encoding.UTF8.GetBytes(xml)), settings);
        return XDocument.Load(reader);
    }

    [Fact]
    public void Record()
    {
        var sb = new StringBuilder();
        sb.AppendLine("M0 SPIKE 2 — DTD handling and entity expansion");
        sb.AppendLine("==============================================");

        sb.AppendLine();
        sb.AppendLine("## Today: XDocument.Parse(string)");
        sb.AppendLine($"  XXE in element text : {Describe(() => XDocument.Parse(XxeInText))}");
        sb.AppendLine($"  jacoco + DOCTYPE    : {Describe(() => XDocument.Parse(JaCoCoWithDoctype))}");
        sb.AppendLine($"  billion laughs      : {Describe(() => XDocument.Parse(BillionLaughs))}");

        sb.AppendLine();
        sb.AppendLine("## XDocument.Load(Stream) — what the issue proposes");
        sb.AppendLine($"  XXE in element text : {Describe(() => XDocument.Load(new MemoryStream(Encoding.UTF8.GetBytes(XxeInText))))}");
        sb.AppendLine($"  jacoco + DOCTYPE    : {Describe(() => XDocument.Load(new MemoryStream(Encoding.UTF8.GetBytes(JaCoCoWithDoctype))))}");
        sb.AppendLine($"  billion laughs      : {Describe(() => XDocument.Load(new MemoryStream(Encoding.UTF8.GetBytes(BillionLaughs))))}");

        var prohibit = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
        };
        sb.AppendLine();
        sb.AppendLine("## DtdProcessing.Prohibit — what the ISSUE suggests");
        sb.AppendLine($"  XXE in element text : {Describe(() => LoadWith(XxeInText, prohibit))}");
        sb.AppendLine($"  jacoco + DOCTYPE    : {Describe(() => LoadWith(JaCoCoWithDoctype, prohibit))}   <-- does real JaCoCo survive?");
        sb.AppendLine($"  billion laughs      : {Describe(() => LoadWith(BillionLaughs, prohibit))}");

        var ignore = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
        };
        sb.AppendLine();
        sb.AppendLine("## DtdProcessing.Ignore — the alternative");
        sb.AppendLine($"  XXE in element text : {Describe(() => LoadWith(XxeInText, ignore))}");
        sb.AppendLine($"  jacoco + DOCTYPE    : {Describe(() => LoadWith(JaCoCoWithDoctype, ignore))}");
        sb.AppendLine($"  billion laughs      : {Describe(() => LoadWith(BillionLaughs, ignore))}");

        File.WriteAllText(OutputPath, sb.ToString());
    }
}
