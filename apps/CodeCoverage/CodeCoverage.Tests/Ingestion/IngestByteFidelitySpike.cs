using System.Text;
using CodeCoverage.Ingestion.Parsing;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// M0 SPIKE — THROWAWAY. Records what today's pipeline does with each byte-level
/// fixture from issue #417's table, before anything is fixed. Deleted once the
/// regression suite it informs exists.
/// </summary>
public class IngestByteFidelitySpike
{
    private static readonly string OutputPath =
        Path.Combine(Path.GetTempPath(), "m0-spike.txt");

    private const string Cobertura = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.75" version="1.9" timestamp="1700000000">
          <packages>
            <package name="MyApp" line-rate="0.75">
              <classes>
                <class name="MyApp.Calculator" filename="src/Calculator.cs" line-rate="0.75">
                  <lines>
                    <line number="10" hits="4" branch="false" />
                    <line number="14" hits="0" branch="false" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    private const string Lcov = """
        TN:
        SF:src/calc.ts
        DA:1,3
        DA:4,0
        end_of_record
        """;

    private const string JaCoCoWithDoctype = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
        <report name="demo">
          <package name="com/example">
            <sourcefile name="Foo.java">
              <line nr="3" mi="0" ci="4" mb="0" cb="0"/>
            </sourcefile>
          </package>
        </report>
        """;

    private const string XxeProbe = """
        <?xml version="1.0"?>
        <!DOCTYPE coverage [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
        <coverage line-rate="1" version="1.9">
          <packages><package name="P"><classes>
            <class name="C" filename="&xxe;"><lines><line number="1" hits="1"/></lines></class>
          </classes></package></packages>
        </coverage>
        """;

    // A small body that expands enormously — the billion-laughs shape.
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
          <packages><package name="P"><classes>
            <class name="C" filename="&e;"><lines><line number="1" hits="1"/></lines></class>
          </classes></package></packages>
        </coverage>
        """;

    /// <summary>The one boundary, reproduced exactly: ParseSessionRecipient.cs:202.</summary>
    private static string DecodeAsServerDoes(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Utf8Bom(string s) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(s)];
    private static byte[] Utf16Le(string s) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s)];

    [Fact]
    public void Record()
    {
        var sb = new StringBuilder();
        sb.AppendLine("M0 SPIKE — behaviour of today's pipeline, per fixture");
        sb.AppendLine("======================================================");

        var factory = new CoverageParserFactory();

        void Probe(string name, byte[] bytes)
        {
            var content = DecodeAsServerDoes(bytes);
            string resolved;
            string outcome;
            try
            {
                var parser = factory.Resolve(content);
                resolved = parser?.FormatName ?? "(none)";
                if (parser is null)
                {
                    outcome = "SKIPPED — no parser claimed it";
                }
                else
                {
                    var result = parser.Parse(content);
                    outcome = $"parsed {result.Files.Count} file(s)"
                        + (result.Files.Count > 0 ? $", first raw path = '{result.Files[0].RawPath}'" : "");
                }
            }
            catch (Exception ex)
            {
                resolved = "(threw during resolve/parse)";
                outcome = $"THREW {ex.GetType().Name}: {ex.Message}";
            }

            sb.AppendLine();
            sb.AppendLine($"### {name}");
            sb.AppendLine($"  first bytes : {string.Join(" ", bytes.Take(6).Select(b => b.ToString("x2")))}");
            sb.AppendLine($"  parser      : {resolved}");
            sb.AppendLine($"  outcome     : {outcome}");
        }

        Probe("cobertura, no BOM (baseline)", Utf8(Cobertura));
        Probe("cobertura + UTF-8 BOM  <-- THIS IS #415", Utf8Bom(Cobertura));
        Probe("cobertura + UTF-16 LE BOM, UTF-16 content", Utf16Le(Cobertura));
        Probe("cobertura + leading blank lines", Utf8("\n\n" + Cobertura));
        Probe("cobertura + trailing NUL padding", [.. Utf8(Cobertura), 0x00, 0x00]);
        Probe("cobertura, CRLF throughout", Utf8(Cobertura.Replace("\n", "\r\n")));
        Probe("empty file (0 bytes)", []);
        Probe("truncated mid-document", Utf8(Cobertura[..(Cobertura.Length / 2)]));
        Probe("lcov, no BOM (baseline)", Utf8(Lcov));
        Probe("lcov + UTF-8 BOM", Utf8Bom(Lcov));
        Probe("jacoco WITH its real DOCTYPE", Utf8(JaCoCoWithDoctype));
        Probe("jacoco + DOCTYPE + UTF-8 BOM", Utf8Bom(JaCoCoWithDoctype));
        Probe("XXE: external entity in filename", Utf8(XxeProbe));
        Probe("billion laughs (entity expansion)", Utf8(BillionLaughs));

        File.WriteAllText(OutputPath, sb.ToString());
    }
}
