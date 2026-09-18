using CodeCoverage.Ingestion.Parsing;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// A coverage server parses XML posted by CI systems, which on a public fork PR is
/// attacker-adjacent input. Issue #417 §4.
/// </summary>
public class XmlIngestSecurityTests
{
    /// <summary>
    /// Measured before the fix (M0 spike, 2026-09-18): this 600-byte document expanded
    /// into a <b>500,000-character</b> attribute value through the parser as it shipped
    /// — and <c>XDocument.Load(Stream)</c>, which the issue proposes, did not change
    /// that. This was a live memory-exhaustion vector, not a theoretical one.
    /// </summary>
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

    private const string ExternalEntity = """
        <?xml version="1.0"?>
        <!DOCTYPE coverage [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
        <coverage line-rate="1" version="1.9">
          <sources><source>&xxe;</source></sources>
          <packages><package name="P"><classes>
            <class name="C" filename="src/a.cs"><lines><line number="1" hits="1"/></lines></class>
          </classes></package></packages>
        </coverage>
        """;

    /// <summary>
    /// A real JaCoCo report carries a DOCTYPE — every one of them does.
    /// <c>DtdProcessing.Prohibit</c>, which issue #417 suggests, rejects this outright
    /// with "For security reasons DTD is prohibited in this XML document", turning a
    /// valid upload into a hard failure for every JaCoCo consumer. Measured both ways
    /// in the M0 spike; this test is what stops the suggestion being adopted later.
    /// </summary>
    private const string JaCoCoWithDoctype = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
        <report name="demo">
          <package name="com/example">
            <sourcefile name="Foo.java"><line nr="3" mi="0" ci="4" mb="0" cb="0"/></sourcefile>
          </package>
        </report>
        """;

    [Fact]
    public void A_real_jacoco_doctype_still_parses()
    {
        var content = ReportContent.FromText(JaCoCoWithDoctype);
        var parser = new JaCoCoParser();

        parser.CanParse(content).Should().BeTrue();
        parser.Parse(content).Files.Single().RawPath.Should().Be("com/example/Foo.java");
    }

    [Fact]
    public void An_external_entity_is_never_resolved()
    {
        var act = () => new CoberturaParser().Parse(ReportContent.FromText(ExternalEntity));

        // Rejected by name rather than silently resolving — and with no file read and
        // no outbound request, because the DTD's entities are never declared.
        act.Should().Throw<System.Xml.XmlException>()
            .WithMessage("*undeclared entity*");
    }

    [Fact]
    public void Entity_expansion_cannot_run_away()
    {
        var act = () => new CoberturaParser().Parse(ReportContent.FromText(BillionLaughs));

        act.Should().Throw<System.Xml.XmlException>()
            .WithMessage("*undeclared entity*");
    }
}
