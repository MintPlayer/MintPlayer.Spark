using System.Text;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files.Sniffing;

/// <summary>What the sniffer made of a file: its stored content type, or why it is refused.</summary>
public sealed record FileSniffResult(string? ContentType, string? Format, string? Error)
{
    public bool IsAccepted => Error is null;

    internal static FileSniffResult Refused(string error) => new(null, null, error);
}

/// <summary>
/// Decides what a file is from its first bytes and its name, and whether an attribute accepts it (#700 D4). The
/// client's MIME type is never consulted; the extension must agree with the content.
/// </summary>
public sealed class FileSniffer
{
    /// <summary>How many leading bytes are read: the signatures' headers, and the markup scan.</summary>
    public const int HeaderLength = 1024;

    /// <summary>What an <c>Image</c> attribute accepts when its <c>fileOptions.accept</c> is absent (§3.6). Never SVG.</summary>
    public static readonly IReadOnlyList<string> DefaultImageAccept = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>The formats every app recognises. Office files are recognised as their ZIP container plus their extension.</summary>
    public static readonly IReadOnlyList<ISparkFileSignature> BuiltIn =
    [
        new MagicBytesSignature("PNG", Types((".png", "image/png")), (0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])),
        new MagicBytesSignature("JPEG", Types((".jpg", "image/jpeg"), (".jpeg", "image/jpeg")), (0, [0xFF, 0xD8, 0xFF])),
        new MagicBytesSignature("GIF", Types((".gif", "image/gif")), (0, "GIF8"u8.ToArray()), (5, "a"u8.ToArray())),
        new MagicBytesSignature("WebP", Types((".webp", "image/webp")), (0, "RIFF"u8.ToArray()), (8, "WEBP"u8.ToArray())),
        new MagicBytesSignature("PDF", Types((".pdf", "application/pdf")), (0, "%PDF-"u8.ToArray())),
        new MagicBytesSignature("Office Open XML", Types(
                (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
                (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
                (".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")),
            // The ZIP container: generators differ in which entry they write first, so the first entry is not checked.
            // Such a file is still served as an attachment under the sandbox CSP, whatever is inside.
            (0, Zip)),
        new MagicBytesSignature("ZIP", Types((".zip", "application/zip")), (0, Zip)),
    ];

    /// <summary>
    /// Markup that makes a file dangerous to serve, whatever else it is: a GIF header followed by a script is still a
    /// script to a browser that sniffs. Checked case-insensitively in the first <see cref="HeaderLength"/> bytes.
    /// </summary>
    private static readonly string[] MarkupMarkers = ["<html", "<script", "<svg", "<!doctype", "<body", "<iframe", "<?xml", "<head", "<object", "<embed"];

    private readonly IReadOnlyList<ISparkFileSignature> signatures;

    public FileSniffer(IEnumerable<ISparkFileSignature>? extra = null)
    {
        var registered = (extra ?? []).ToList();
        foreach (var signature in registered)
        {
            if (signature.HeaderBytes > HeaderLength)
                throw new ArgumentException($"The file signature '{signature.Name}' needs {signature.HeaderBytes} header bytes; at most {HeaderLength} are read.");
        }
        // Registered signatures first: an app's more specific format wins over a built-in container.
        signatures = [.. registered, .. BuiltIn];
    }

    public IReadOnlyList<ISparkFileSignature> Signatures => signatures;

    /// <summary>Whether some signature recognises an accept entry (a MIME type or <c>.ext</c>): D4's startup check.</summary>
    public bool IsRecognised(string acceptEntry)
        => acceptEntry.StartsWith('.')
            ? signatures.Any(s => s.Extensions.ContainsKey(acceptEntry))
            : signatures.Any(s => s.Extensions.Values.Contains(acceptEntry, StringComparer.OrdinalIgnoreCase));

    /// <summary>The accept list in force: the attribute's, or for an <c>Image</c> without one the raster default. Empty refuses all.</summary>
    public static IReadOnlyList<string> EffectiveAccept(string dataType, SparkFileOptions? options)
        => options?.Accept is { } accept
            ? accept
            : dataType == SparkFileDataTypes.Image ? DefaultImageAccept : [];

    /// <summary>Recognises <paramref name="header"/> named <paramref name="fileName"/>, and checks it against <paramref name="accept"/>.</summary>
    public FileSniffResult Sniff(ReadOnlySpan<byte> header, string fileName, IReadOnlyList<string> accept)
    {
        if (accept.Count == 0)
            return FileSniffResult.Refused("This attribute accepts no files.");
        if (header.Length == 0)
            return FileSniffResult.Refused("The file is empty.");
        if (ContainsMarkup(header))
            return FileSniffResult.Refused("The file contains markup (HTML, SVG or XML) and is refused.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var matching = new List<ISparkFileSignature>();
        foreach (var candidate in signatures)
        {
            if (candidate.Matches(header))
                matching.Add(candidate);
        }
        if (matching.Count == 0)
            return FileSniffResult.Refused("The file's content is not a recognised format.");

        var signature = matching.FirstOrDefault(s => s.Extensions.ContainsKey(extension));
        if (signature is null)
            return FileSniffResult.Refused($"The file's content is {matching[0].Name}, which does not match its extension '{extension}'.");

        var contentType = signature.Extensions[extension];
        var accepted = accept.Any(entry => entry.StartsWith('.')
            ? string.Equals(entry, extension, StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry, contentType, StringComparison.OrdinalIgnoreCase));
        return accepted
            ? new FileSniffResult(contentType, signature.Name, null)
            : FileSniffResult.Refused($"{signature.Name} files are not accepted here.");
    }

    private static bool ContainsMarkup(ReadOnlySpan<byte> header)
    {
        // Latin-1 keeps one char per byte, so a marker split by nothing but bytes is found as written.
        var text = Encoding.Latin1.GetString(header[..Math.Min(header.Length, HeaderLength)]);
        foreach (var marker in MarkupMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static Dictionary<string, string> Types(params (string Extension, string ContentType)[] entries)
        => entries.ToDictionary(e => e.Extension, e => e.ContentType, StringComparer.OrdinalIgnoreCase);
}
