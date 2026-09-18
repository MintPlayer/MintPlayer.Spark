using System.Text;
using CodeCoverage.Ingestion.Parsing;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Issue #417's tolerance table, byte-level rather than pretty-printed — the whole
/// class of bug lives below the syntax, so a fixture written as a C# string literal
/// would test nothing. Every case here is a shape a real collector emits.
/// </summary>
public class ReportContentTests
{
    private const string Cobertura = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" version="1.9">
          <packages><package name="MyApp"><classes>
            <class name="C" filename="src/Calculator.cs">
              <lines><line number="10" hits="4" /><line number="14" hits="0" /></lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private const string Lcov = "TN:\nSF:src/calc.ts\nDA:1,3\nDA:4,0\nend_of_record\n";

    private static byte[] WithUtf8Bom(string s) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(s)];

    /// <summary>
    /// THE #415 REGRESSION. `Microsoft.Testing.Extensions.CodeCoverage` writes
    /// EF BB BF before &lt;?xml. A BOM is legal in a UTF-8 XML document; decoding the
    /// bytes with a bare Encoding.UTF8.GetString left it attached as U+FEFF and
    /// XDocument.Parse threw "Data at the root level is invalid. Line 1, position 1."
    /// — which finalized the build CompleteWithErrors with zero files measured while
    /// the consumer's CI stayed green.
    /// </summary>
    [Fact]
    public void Utf8_bom_parses_identically_to_the_same_bytes_without_one()
    {
        var withBom = ReportContent.FromBytes(WithUtf8Bom(Cobertura));
        var without = ReportContent.FromBytes(Encoding.UTF8.GetBytes(Cobertura));

        withBom.Text.Should().Be(without.Text);

        var parser = new CoberturaParser();
        parser.CanParse(withBom).Should().BeTrue();
        parser.Parse(withBom).Files.Single().RawPath.Should().Be("src/Calculator.cs");
    }

    /// <summary>
    /// The same BOM through a completely different parser. lcov's sniff is an ordinal
    /// StartsWith("SF:"/"TN:"), so a BOM did not throw there — it failed detection,
    /// the file was skipped with a server-side warning, and the build measured zero.
    /// Same silent outcome, different code path: the reason this normalisation belongs
    /// at the byte→text boundary rather than in any one parser.
    /// </summary>
    [Fact]
    public void Utf8_bom_on_lcov_still_matches_the_SF_prefix()
    {
        var content = ReportContent.FromBytes(WithUtf8Bom(Lcov));
        var parser = new LcovParser();

        parser.CanParse(content).Should().BeTrue();
        parser.Parse(content).Files.Single().RawPath.Should().Be("src/calc.ts");
    }

    [Fact]
    public void Utf16_le_bom_decodes_and_parses()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Cobertura)];
        var content = ReportContent.FromBytes(bytes);

        new CoberturaParser().CanParse(content).Should().BeTrue();
        new CoberturaParser().Parse(content).Files.Single().RawPath.Should().Be("src/Calculator.cs");
    }

    [Fact]
    public void Utf16_be_bom_decodes_and_parses()
    {
        byte[] bytes = [0xFE, 0xFF, .. new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes(Cobertura)];
        var content = ReportContent.FromBytes(bytes);

        new CoberturaParser().CanParse(content).Should().BeTrue();
    }

    /// <summary>
    /// Measured in the M0 spike: parsing from a Stream — which issue #417 proposes as
    /// covering "most of the list for free" — does NOT fix this. It throws "Unexpected
    /// XML declaration. The XML declaration must be the first node in the document."
    /// It needs an explicit trim, which is why the boundary decodes and trims rather
    /// than handing a reader the raw stream.
    /// </summary>
    [Fact]
    public void Leading_blank_lines_before_the_declaration_parse()
    {
        var content = ReportContent.FromBytes(Encoding.UTF8.GetBytes("\n\n" + Cobertura));

        new CoberturaParser().CanParse(content).Should().BeTrue();
        new CoberturaParser().Parse(content).Files.Single().RawPath.Should().Be("src/Calculator.cs");
    }

    /// <summary>
    /// Some writers pad to a block boundary. Also measured as NOT fixed by stream
    /// parsing: "'.', hexadecimal value 0x00, is an invalid character."
    /// </summary>
    [Fact]
    public void Trailing_nul_padding_parses()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes(Cobertura), 0x00, 0x00, 0x00];
        var content = ReportContent.FromBytes(bytes);

        new CoberturaParser().Parse(content).Files.Single().RawPath.Should().Be("src/Calculator.cs");
    }

    [Fact]
    public void Crlf_line_endings_parse_for_xml_and_for_lcov()
    {
        var xml = ReportContent.FromBytes(Encoding.UTF8.GetBytes(Cobertura.Replace("\n", "\r\n")));
        var lcov = ReportContent.FromBytes(Encoding.UTF8.GetBytes(Lcov.Replace("\n", "\r\n")));

        new CoberturaParser().Parse(xml).Files.Single().RawPath.Should().Be("src/Calculator.cs");
        new LcovParser().Parse(lcov).Files.Single().RawPath.Should().Be("src/calc.ts");
    }

    [Fact]
    public void An_empty_upload_is_empty_rather_than_a_parse_failure()
    {
        ReportContent.FromBytes([]).IsEmpty.Should().BeTrue();
        ReportContent.FromBytes(Encoding.UTF8.GetBytes("   \r\n  ")).IsEmpty.Should().BeTrue();
        // A BOM and nothing else is still nothing — a real shape from a crashed writer.
        ReportContent.FromBytes([0xEF, 0xBB, 0xBF]).IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// A declared non-UTF-8 encoding with no BOM. The declaration is read from the
    /// leading bytes as ASCII, before any decode decision — it cannot assume an
    /// encoding in order to read the thing that names the encoding.
    ///
    /// <para>The limit of that approach, stated rather than discovered later: it only
    /// reaches an ASCII-compatible prolog. A UTF-16 document with no BOM is
    /// unreadable this way and stays UTF-8-by-default — which is exactly today's
    /// behaviour, so it is a known gap and not a regression. Every UTF-16 writer in
    /// practice emits a BOM, which the branch above handles.</para>
    /// </summary>
    [Fact]
    public void A_declared_encoding_is_honoured_when_there_is_no_bom()
    {
        // Latin-1 is built in; the é is one byte here and would decode as U+FFFD
        // through UTF-8, so the filename proves which decoder actually ran.
        var declared = Cobertura
            .Replace("encoding=\"utf-8\"", "encoding=\"iso-8859-1\"")
            .Replace("src/Calculator.cs", "src/Café.cs");
        var bytes = Encoding.Latin1.GetBytes(declared);

        var content = ReportContent.FromBytes(bytes);

        new CoberturaParser().CanParse(content).Should().BeTrue();
        new CoberturaParser().Parse(content).Files.Single().RawPath.Should().Be("src/Café.cs");
    }

    /// <summary>
    /// An unknown or unregistered code page must not fail an upload over a label —
    /// most single-byte pages need CodePagesEncodingProvider, which is not registered.
    /// Degrade to UTF-8 and parse what we can.
    /// </summary>
    [Fact]
    public void An_unresolvable_declared_encoding_degrades_to_utf8_rather_than_throwing()
    {
        var declared = Cobertura.Replace("encoding=\"utf-8\"", "encoding=\"x-not-a-real-charset\"");
        var content = ReportContent.FromBytes(Encoding.UTF8.GetBytes(declared));

        new CoberturaParser().CanParse(content).Should().BeTrue();
    }
}
