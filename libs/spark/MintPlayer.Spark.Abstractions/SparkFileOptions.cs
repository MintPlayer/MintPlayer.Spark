using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The <c>fileOptions</c> of a <c>BinaryFile</c> or <c>Image</c> attribute in the model file (#700 D16).
/// Hand-authored: the synchronizer never writes it, so it survives every synchronize.
/// </summary>
/// <remarks>
/// <para>
/// It gates writes, so it is <b>structural</b>: part of the model hash (<c>ModelFileShape</c>). Editing it
/// on a deployed server without re-running <c>--spark-synchronize-model</c> stops the app outside
/// Development, so a limit cannot be loosened quietly.
/// </para>
/// <para>
/// Configuration (<c>Spark:Files</c>) can only tighten these values, never widen them.
/// </para>
/// </remarks>
public sealed class SparkFileOptions
{
    /// <summary>
    /// The accepted types: MIME types (<c>application/pdf</c>, <c>image/png</c>) or extensions with a dot
    /// (<c>.pdf</c>). Every entry must be recognisable from the bytes; <c>*</c> and wildcards are refused.
    /// <para>
    /// A <c>BinaryFile</c> without an accept list refuses every upload (fail-closed, D4). An <c>Image</c>
    /// defaults to PNG, JPEG, GIF and WebP, and can only narrow that set: SVG is never an Image.
    /// </para>
    /// </summary>
    public string[]? Accept { get; set; }

    /// <summary>The largest accepted file, in bytes. Capped by the global <c>Spark:Files:MaxFileBytes</c>.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>The most files a list attribute (<c>isArray</c>) may hold. Default 10. Only valid on a list.</summary>
    public int? MaxCount { get; set; }

    /// <summary>
    /// An <c>Image</c>'s downscaled copies: variant name → longest edge in pixels. Absent means the
    /// defaults (<c>thumb</c> 160, <c>medium</c> 800); an empty object turns variants off.
    /// </summary>
    public Dictionary<string, int>? Variants { get; set; }

    /// <summary>
    /// The largest <c>Image</c> accepted, in pixels (width × height), read from the header before anything is decoded.
    /// Capped by the global <c>Spark:Files:MaxImagePixels</c> (default 40 million): it can only tighten that.
    /// </summary>
    public long? MaxPixels { get; set; }

    /// <summary>How downloads are served. Absent means the store's default (redirect for a bucket, proxy otherwise).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<SparkFileDelivery>))]
    public SparkFileDelivery? Delivery { get; set; }

    /// <summary>The smallest and largest variant edge accepted (pixels).</summary>
    public const int MinVariantEdge = 16, MaxVariantEdge = 4096;

    /// <summary>
    /// The shape problems of <paramref name="options"/> on an attribute, as messages naming
    /// <paramref name="where"/> (<c>Car.Photos</c>). Empty when it is well-formed. Whether each accept entry
    /// is a type the server can recognise, and the global ceiling, are checked by the Files plugin.
    /// </summary>
    public static IEnumerable<string> ShapeErrors(SparkFileOptions? options, string where, string dataType, bool isArray)
    {
        if (options is null) yield break;

        if (!SparkFileDataTypes.IsFile(dataType))
        {
            yield return $"{where}: fileOptions is only valid on a BinaryFile or Image attribute (dataType is '{dataType}').";
            yield break;
        }

        foreach (var entry in options.Accept ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                yield return $"{where}: fileOptions.accept has an empty entry.";
            else if (entry.Contains('*'))
                yield return $"{where}: fileOptions.accept entry '{entry}' is a wildcard; name each type (wildcards are refused, #700 D4).";
            else if (!entry.StartsWith('.') && !entry.Contains('/'))
                yield return $"{where}: fileOptions.accept entry '{entry}' is neither a MIME type (type/subtype) nor an extension (.ext).";
            else if (dataType == SparkFileDataTypes.Image && IsSvg(entry))
                yield return $"{where}: fileOptions.accept entry '{entry}' is SVG, which is never an Image (it can carry script). Use a BinaryFile served as a download.";
        }

        if (options.MaxBytes is <= 0)
            yield return $"{where}: fileOptions.maxBytes must be positive.";

        if (options.MaxCount is not null)
        {
            if (!isArray)
                yield return $"{where}: fileOptions.maxCount is only valid on a list attribute.";
            else if (options.MaxCount <= 0)
                yield return $"{where}: fileOptions.maxCount must be positive.";
        }

        if (options.MaxPixels is not null)
        {
            if (dataType != SparkFileDataTypes.Image)
                yield return $"{where}: fileOptions.maxPixels is only valid on an Image attribute.";
            else if (options.MaxPixels <= 0)
                yield return $"{where}: fileOptions.maxPixels must be positive.";
        }

        if (options.Variants is not null)
        {
            if (dataType != SparkFileDataTypes.Image)
                yield return $"{where}: fileOptions.variants is only valid on an Image attribute.";
            else
                foreach (var (name, edge) in options.Variants)
                {
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                        yield return $"{where}: fileOptions.variants name '{name}' may only hold letters, digits, '-' and '_' (at most 64).";
                    if (edge is < MinVariantEdge or > MaxVariantEdge)
                        yield return $"{where}: fileOptions.variants '{name}' is {edge} px; it must be between {MinVariantEdge} and {MaxVariantEdge}.";
                }
        }
    }

    private static bool IsSvg(string entry)
        => entry.Equals(".svg", StringComparison.OrdinalIgnoreCase)
        || entry.Equals(".svgz", StringComparison.OrdinalIgnoreCase)
        || entry.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How a file attribute's downloads are served (#700 D14).</summary>
public enum SparkFileDelivery
{
    /// <summary>Spark checks the read, then redirects to a short-lived presigned URL (bucket stores).</summary>
    Redirect,

    /// <summary>Spark checks the read and streams the bytes itself.</summary>
    Proxy,
}
