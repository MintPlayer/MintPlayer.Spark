using System.Text;

namespace CodeCoverage.Ingestion.Parsing;

/// <summary>
/// The single place uploaded report bytes become text, for every format.
///
/// Issue #415 was a UTF-8 BOM: the bytes were decoded with a bare
/// <c>Encoding.UTF8.GetString</c>, the BOM survived as a leading U+FEFF, and
/// <c>XDocument.Parse</c> threw "Data at the root level is invalid. Line 1,
/// position 1." The same BOM defeats lcov's <c>SF:</c> sniff instead of throwing,
/// which is the same silent zero-files outcome through a different parser — so
/// this normalisation belongs here rather than in any one parser.
///
/// Measured (M0 spike, 2026-09-18) against the real parsers: parsing from a
/// Stream, as issue #417 proposes, fixes the BOM but NOT leading whitespace
/// ("Unexpected XML declaration…") and NOT trailing NUL padding ("hexadecimal
/// value 0x00, is an invalid character"). Both of those are legal-ish things real
/// writers emit and both need an explicit trim, so we decode and trim here and
/// hand the parsers text. A TextReader-based XmlReader ignores the prolog's
/// encoding attribute, which is why the declared encoding is resolved here too
/// rather than left to the reader.
/// </summary>
public sealed class ReportContent
{
    private ReportContent(string text, int byteCount, Encoding encoding)
    {
        Text = text;
        ByteCount = byteCount;
        Encoding = encoding;
    }

    /// <summary>Decoded, BOM-free, whitespace- and NUL-trimmed report text.</summary>
    public string Text { get; }

    /// <summary>Size of the decompressed upload, for diagnostics and bounds.</summary>
    public int ByteCount { get; }

    /// <summary>The encoding the bytes were decoded with, for diagnostics.</summary>
    public Encoding Encoding { get; }

    public bool IsEmpty => Text.Length == 0;

    public static ReportContent FromBytes(ReadOnlySpan<byte> bytes)
    {
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = encoding.GetString(bytes[preambleLength..]);

        // A BOM can also survive a correct decode when the producer wrote one
        // *and* declared the encoding (double preamble), so strip any remaining
        // U+FEFF defensively rather than trusting the byte-level skip alone.
        text = text.TrimStart('﻿');

        // Leading whitespace before <?xml is illegal in XML and some writers emit
        // it; trailing NUL padding comes from writers that pad to a block boundary.
        // Neither is fixed by parsing from a stream — both are trimmed here.
        return new ReportContent(text.Trim('\0', ' ', '\t', '\r', '\n'), bytes.Length, encoding);
    }

    /// <summary>
    /// BOM first, then the XML declaration's own <c>encoding=</c>, then UTF-8.
    /// Never throws: an unresolvable encoding name degrades to UTF-8 rather than
    /// failing an upload over a label.
    /// </summary>
    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(false);
        }
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false);
        }
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        }

        preambleLength = 0;

        var declared = ReadDeclaredEncoding(bytes);
        if (declared is not null)
        {
            try
            {
                var encoding = Encoding.GetEncoding(declared);
                // UTF-8 is the default anyway; returning the framework's instance
                // keeps the diagnostic name honest without a lookup cost.
                return encoding;
            }
            catch (ArgumentException)
            {
                // An unknown or unregistered code page (most single-byte pages need
                // CodePagesEncodingProvider). Fall through to UTF-8 rather than
                // rejecting an upload we can very probably still read.
            }
        }

        return new UTF8Encoding(false);
    }

    /// <summary>
    /// Scans the leading bytes as ASCII for <c>encoding="…"</c> inside the XML
    /// declaration. Deliberately byte-level and bounded: it runs before any decode
    /// decision has been made, so it cannot assume an encoding, and a declaration
    /// that is not in the first 512 bytes is not a declaration.
    /// </summary>
    private static string? ReadDeclaredEncoding(ReadOnlySpan<byte> bytes)
    {
        var window = bytes[..Math.Min(bytes.Length, 512)];
        if (window.Length < 6) return null;

        // Only an ASCII-compatible prolog is readable this way; a UTF-16 document
        // without a BOM is out of reach here and stays UTF-8-by-default, which is
        // the same as today's behaviour rather than a regression.
        Span<char> chars = stackalloc char[window.Length];
        for (var i = 0; i < window.Length; i++)
            chars[i] = (char)window[i];

        var header = new string(chars);
        var declarationEnd = header.IndexOf("?>", StringComparison.Ordinal);
        if (!header.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) || declarationEnd < 0)
            return null;

        var declaration = header[..declarationEnd];
        var at = declaration.IndexOf("encoding", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var quote = declaration.IndexOfAny(['"', '\''], at);
        if (quote < 0) return null;

        var close = declaration.IndexOf(declaration[quote], quote + 1);
        if (close < 0) return null;

        var name = declaration[(quote + 1)..close].Trim();
        return name.Length == 0 ? null : name;
    }
}
