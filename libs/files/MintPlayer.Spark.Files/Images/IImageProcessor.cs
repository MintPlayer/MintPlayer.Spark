namespace MintPlayer.Spark.Files.Images;

/// <summary>
/// Decodes an uploaded <c>Image</c> and re-encodes it (#700 PRD §3.6, SP5). Register one with
/// <c>files.UseSkiaSharpImages()</c> (<c>MintPlayer.Spark.Files.Images.SkiaSharp</c>, MIT),
/// <c>files.UseNetVipsImages()</c> (<c>MintPlayer.Spark.Files.Images.NetVips</c>, LGPL) or
/// <see cref="FilesBuilder.UseImageProcessor{TProcessor}()"/>. Without one, an image is stored as uploaded, with no variants.
/// </summary>
/// <remarks>
/// Every implementation must, and the shared conformance suite checks that it does:
/// <list type="bullet">
/// <item>read the dimensions from the header and throw <see cref="ImageRejectedException"/> above
/// <see cref="ImageProcessingRequest.MaxPixels"/> <b>before decoding a pixel</b> (the decompression-bomb guard);</item>
/// <item>apply the EXIF orientation, so <see cref="ProcessedImage.Width"/>/<see cref="ProcessedImage.Height"/> are as displayed;</item>
/// <item>re-encode: never return the input bytes. The output carries no metadata (EXIF, GPS, XMP, comments) and nothing
/// appended after the image data;</item>
/// <item>decode only the first frame of an animation, and encode it as a still (a GIF becomes a PNG);</item>
/// <item>make each variant from the one decoded bitmap, never upscaled: a variant is no larger than the original;</item>
/// <item>throw <see cref="ImageRejectedException"/> for input it cannot decode. Any other exception also refuses the
/// upload (fail-closed), with a generic message.</item>
/// </list>
/// It is a singleton and may be called concurrently.
/// </remarks>
public interface IImageProcessor
{
    /// <param name="source">The sniffed upload: seekable, positioned at 0, owned by the caller.</param>
    /// <param name="request">What to produce.</param>
    /// <param name="cancellationToken">Cancels the processing.</param>
    Task<ProcessedImage> ProcessAsync(Stream source, ImageProcessingRequest request, CancellationToken cancellationToken);
}

/// <param name="ContentType">The type the sniffer detected: <c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c> or <c>image/webp</c>.</param>
/// <param name="MaxPixels">The pixel cap (width × height), enforced from the header.</param>
/// <param name="Variants">Variant name → longest edge in pixels. Empty for none.</param>
public sealed record ImageProcessingRequest(string ContentType, long MaxPixels, IReadOnlyDictionary<string, int> Variants);

/// <summary>The re-encoded image and its variants. Disposing it disposes every stream it holds.</summary>
public sealed class ProcessedImage(string contentType, int width, int height, Stream content, IReadOnlyList<ProcessedImageVariant> variants) : IAsyncDisposable
{
    /// <summary>The re-encoded original's type: <c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c>.</summary>
    public string ContentType { get; } = contentType;

    /// <summary>Width in pixels, after orientation.</summary>
    public int Width { get; } = width;

    /// <summary>Height in pixels, after orientation.</summary>
    public int Height { get; } = height;

    /// <summary>The re-encoded original, readable from its start.</summary>
    public Stream Content { get; } = content;

    public IReadOnlyList<ProcessedImageVariant> Variants { get; } = variants;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        foreach (var variant in Variants)
            await variant.Content.DisposeAsync();
    }
}

/// <param name="Name">The requested variant name.</param>
/// <param name="ContentType">The encoded type (<c>image/webp</c> for the shipped processors).</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Content">The encoded bytes, readable from their start.</param>
public sealed record ProcessedImageVariant(string Name, string ContentType, int Width, int Height, Stream Content);

/// <summary>An image the processor refuses: over the pixel cap, or not decodable. The message is shown to the uploader.</summary>
public sealed class ImageRejectedException(string message) : Exception(message)
{
    public static ImageRejectedException TooManyPixels(long width, long height, long maxPixels)
        => new($"The image is {width}×{height} pixels; at most {maxPixels:N0} pixels are accepted.");

    public static ImageRejectedException Undecodable()
        => new("The image could not be read.");
}

/// <summary>Shared arithmetic for processors, so every implementation sizes alike.</summary>
public static class ImageProcessing
{
    /// <summary>Whether the header's dimensions are acceptable; refuse before decoding when not.</summary>
    public static void EnsureWithinPixels(long width, long height, long maxPixels)
    {
        if (width <= 0 || height <= 0)
            throw ImageRejectedException.Undecodable();
        if (width * height > maxPixels)
            throw ImageRejectedException.TooManyPixels(width, height, maxPixels);
    }

    /// <summary>The variant's size: the longest edge scaled to <paramref name="edge"/>, never upscaled, at least 1 px.</summary>
    public static (int Width, int Height) Fit(int width, int height, int edge)
    {
        var longest = Math.Max(width, height);
        if (longest <= edge)
            return (width, height);
        var scale = (double)edge / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>The type the original is re-encoded to: a GIF (animation or not) becomes a PNG still; others keep theirs.</summary>
    public static string OutputContentType(string sourceContentType) => sourceContentType switch
    {
        "image/jpeg" => "image/jpeg",
        "image/webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>The JPEG/WebP quality of a re-encoded original.</summary>
    public const int OriginalQuality = 90;

    /// <summary>The WebP quality of a variant (SP5).</summary>
    public const int VariantQuality = 80;
}
