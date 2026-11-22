using Microsoft.Net.Http.Headers;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files.Storage;

/// <summary>
/// How a stored file is served, wherever its bytes come from (#700 §3.5, D8): Spark's own responses and the response a
/// store's presigned URL asks its server for.
/// </summary>
public static class SparkFileServing
{
    /// <summary>Never shared, never kept: a URL outliving its right would otherwise outlive it in a cache too.</summary>
    public const string CacheControl = "private, no-store";

    /// <summary>Nothing in a served file runs as a page, even when a browser is talked into rendering it.</summary>
    public const string ContentSecurityPolicy = "sandbox; default-src 'none'";

    /// <summary>The raster types an <c>Image</c> attribute may show inline. Everything else is an attachment.</summary>
    public static readonly IReadOnlySet<string> InlineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    /// <summary>
    /// Inline only for a raster image of an <c>Image</c> attribute (a <see cref="SparkImage"/>) or one of its variants; a
    /// <c>BinaryFile</c> that happens to be a PNG still downloads.
    /// </summary>
    public static bool IsInline(SparkFile file, string? variant)
        => file is SparkImage image && InlineTypes.Contains(ContentType(image, variant));

    /// <summary>The stored, sniffed type of the file or its variant.</summary>
    public static string ContentType(SparkFile file, string? variant)
        => variant is not null && file is SparkImage image && image.Variants.TryGetValue(variant, out var v) ? v.ContentType : file.ContentType;

    /// <summary>
    /// The <c>Content-Disposition</c> value: <c>inline</c> or <c>attachment</c>, with an ASCII <c>filename</c> and the exact
    /// <c>filename*=UTF-8''…</c>.
    /// </summary>
    public static string ContentDisposition(string fileName, bool inline)
    {
        var header = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        header.SetHttpFileName(string.IsNullOrWhiteSpace(fileName) ? "file" : fileName);
        return header.ToString();
    }

    /// <summary>The entity tag: the file's SHA-256, or the variant's own id (a store key never holds other bytes).</summary>
    public static string ETag(SparkFile file, string? variant)
        => variant is null && file.Sha256 is { Length: > 0 } sha ? sha : VariantOrFileId(file, variant);

    /// <summary>The id of the stored object: the variant's when one is asked for, else the file's.</summary>
    public static string VariantOrFileId(SparkFile file, string? variant)
        => variant is not null && file is SparkImage image && image.Variants.TryGetValue(variant, out var v) ? v.FileId : file.FileId;
}
