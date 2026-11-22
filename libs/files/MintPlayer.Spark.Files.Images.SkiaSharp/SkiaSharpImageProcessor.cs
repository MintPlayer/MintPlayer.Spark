using MintPlayer.Spark.Files.Images;
using SkiaSharp;

namespace MintPlayer.Spark.Files.Images.SkiaSharp;

/// <summary>
/// The default <see cref="IImageProcessor"/> (MIT, PRD SP5): reads the header (<see cref="SKCodec"/>) and refuses an image over
/// the pixel cap before decoding, decodes the first frame once, applies the EXIF orientation by hand (Skia does not), and
/// re-encodes the original and every variant from that one bitmap. Skia's encoders write no metadata.
/// </summary>
public sealed class SkiaSharpImageProcessor : IImageProcessor
{
    public Task<ProcessedImage> ProcessAsync(Stream source, ImageProcessingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        // Skia is synchronous and CPU-bound; the commit is already off the request's critical path for anything else.
        return Task.Run(() => Process(source, request, cancellationToken), cancellationToken);
    }

    private static ProcessedImage Process(Stream source, ImageProcessingRequest request, CancellationToken cancellationToken)
    {
        // The managed stream stays the caller's: Skia only reads it.
        using var skStream = new SKManagedStream(source, disposeManagedStream: false);
        using var codec = SKCodec.Create(skStream, out var created)
            ?? throw ImageRejectedException.Undecodable();
        if (created != SKCodecResult.Success || !Matches(codec.EncodedFormat, request.ContentType))
            throw ImageRejectedException.Undecodable();

        // The bomb guard: dimensions from the header, nothing decoded yet.
        var info = codec.Info;
        ImageProcessing.EnsureWithinPixels(info.Width, info.Height, request.MaxPixels);
        cancellationToken.ThrowIfCancellationRequested();

        var alpha = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, alpha);
        using var decoded = new SKBitmap(decodeInfo);
        // Frame 0 only (the default options): an animation becomes its first frame. A truncated file is refused, not padded.
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels(), new SKCodecOptions(0));
        if (result != SKCodecResult.Success)
            throw ImageRejectedException.Undecodable();
        cancellationToken.ThrowIfCancellationRequested();

        using var oriented = Orient(decoded, codec.EncodedOrigin);
        var bitmap = oriented ?? decoded;
        using var image = SKImage.FromBitmap(bitmap);

        var outputType = ImageProcessing.OutputContentType(request.ContentType);
        var content = Encode(image, outputType, ImageProcessing.OriginalQuality);

        var variants = new List<ProcessedImageVariant>(request.Variants.Count);
        try
        {
            foreach (var (name, edge) in request.Variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (width, height) = ImageProcessing.Fit(bitmap.Width, bitmap.Height, edge);
                using var scaled = Scale(image, width, height, alpha);
                variants.Add(new ProcessedImageVariant(name, "image/webp", width, height, Encode(scaled, "image/webp", ImageProcessing.VariantQuality)));
            }
        }
        catch
        {
            content.Dispose();
            foreach (var variant in variants)
                variant.Content.Dispose();
            throw;
        }
        return new ProcessedImage(outputType, bitmap.Width, bitmap.Height, content, variants);
    }

    private static bool Matches(SKEncodedImageFormat format, string contentType) => (format, contentType) switch
    {
        (SKEncodedImageFormat.Png, "image/png") => true,
        (SKEncodedImageFormat.Jpeg, "image/jpeg") => true,
        (SKEncodedImageFormat.Gif, "image/gif") => true,
        (SKEncodedImageFormat.Webp, "image/webp") => true,
        _ => false,
    };

    /// <summary>
    /// The bitmap as displayed, or null when it already is (<see cref="SKEncodedOrigin.TopLeft"/>). Each matrix maps a stored
    /// pixel to its displayed place (Skia's <c>SkEncodedOriginToMatrix</c>); origins 5–8 swap width and height.
    /// </summary>
    internal static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        SKMatrix matrix;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: matrix = new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1); break;
            case SKEncodedOrigin.BottomRight: matrix = new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1); break;
            case SKEncodedOrigin.BottomLeft: matrix = new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1); break;
            case SKEncodedOrigin.LeftTop: matrix = new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1); break;
            case SKEncodedOrigin.RightTop: matrix = new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1); break;
            case SKEncodedOrigin.RightBottom: matrix = new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1); break;
            case SKEncodedOrigin.LeftBottom: matrix = new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1); break;
            default: return null;
        }
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = source.Info.WithSize(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        var target = new SKBitmap(info);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return target;
    }

    /// <summary>A downscaled copy: linear filtering over mipmaps, so a large reduction does not alias.</summary>
    private static SKImage Scale(SKImage image, int width, int height, SKAlphaType alpha)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha))
            ?? throw new InvalidOperationException("Skia could not allocate the variant's surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    private static MemoryStream Encode(SKImage image, string contentType, int quality)
    {
        var format = contentType switch
        {
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        using var data = image.Encode(format, quality)
            ?? throw new InvalidOperationException($"Skia could not encode {contentType}.");
        var stream = new MemoryStream(capacity: (int)data.Size);
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }
}
