using MintPlayer.Spark.Files.Images;
using NetVips;
using VipsImage = NetVips.Image;

namespace MintPlayer.Spark.Files.Images.NetVips;

/// <summary>
/// An <see cref="IImageProcessor"/> on libvips (PRD SP5). libvips opens an image lazily, so the header's dimensions are checked
/// against the pixel cap before a pixel is decoded. Then the first page is decoded once, autorotated, re-encoded with
/// <c>keep: none</c> (no EXIF, XMP, ICC or comments), and each variant is made from that one decoded image
/// (<c>thumbnail_image</c>, never the per-variant <c>thumbnail</c>, which re-opens the file).
/// </summary>
public sealed class NetVipsImageProcessor : IImageProcessor
{
    private static readonly Lazy<bool> Initialised = new(() =>
    {
        // Loaders marked untrusted (ImageMagick, PDF, SVG, matrix, ...) are refused outright: only the four formats the sniffer
        // admits reach libvips, and the loader name is checked as well.
        global::NetVips.NetVips.BlockUntrusted = true;
        // No operation cache: uploads are never processed twice, and the cache would keep their pixels alive.
        Cache.Max = 0;
        return true;
    });

    public Task<ProcessedImage> ProcessAsync(Stream source, ImageProcessingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        _ = Initialised.Value;
        return Task.Run(() => Process(source, request, cancellationToken), cancellationToken);
    }

    private static ProcessedImage Process(Stream source, ImageProcessingRequest request, CancellationToken cancellationToken)
    {
        VipsImage header;
        try
        {
            // Lazy: this reads the header only. failOn Error refuses a truncated or corrupt file instead of padding it.
            header = VipsImage.NewFromStream(source, access: Enums.Access.Random, failOn: Enums.FailOn.Error);
        }
        catch (VipsException)
        {
            throw ImageRejectedException.Undecodable();
        }

        using (header)
        {
            if (!Matches(header.Contains("vips-loader") ? header.Get("vips-loader") as string : null, request.ContentType))
                throw ImageRejectedException.Undecodable();
            ImageProcessing.EnsureWithinPixels(header.Width, header.Height, request.MaxPixels);
            cancellationToken.ThrowIfCancellationRequested();

            VipsImage decoded;
            try
            {
                using var rotated = header.Autorot();
                // Decoded once, here; every encode below reads this memory image.
                decoded = rotated.CopyMemory();
            }
            catch (VipsException)
            {
                throw ImageRejectedException.Undecodable();
            }

            using (decoded)
            {
                var outputType = ImageProcessing.OutputContentType(request.ContentType);
                var content = new MemoryStream(Encode(decoded, outputType, ImageProcessing.OriginalQuality));
                var variants = new List<ProcessedImageVariant>(request.Variants.Count);
                try
                {
                    foreach (var (name, edge) in request.Variants)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (width, height) = ImageProcessing.Fit(decoded.Width, decoded.Height, edge);
                        using var scaled = width == decoded.Width && height == decoded.Height
                            ? decoded.Copy()
                            : decoded.Resize((double)width / decoded.Width, vscale: (double)height / decoded.Height, kernel: Enums.Kernel.Lanczos3);
                        variants.Add(new ProcessedImageVariant(name, "image/webp", scaled.Width, scaled.Height,
                            new MemoryStream(Encode(scaled, "image/webp", ImageProcessing.VariantQuality))));
                    }
                }
                catch
                {
                    content.Dispose();
                    foreach (var variant in variants)
                        variant.Content.Dispose();
                    throw;
                }
                return new ProcessedImage(outputType, decoded.Width, decoded.Height, content, variants);
            }
        }
    }

    private static bool Matches(string? loader, string contentType) => (loader, contentType) switch
    {
        ("pngload_source" or "pngload", "image/png") => true,
        ("jpegload_source" or "jpegload", "image/jpeg") => true,
        ("gifload_source" or "gifload", "image/gif") => true,
        ("webpload_source" or "webpload", "image/webp") => true,
        _ => false,
    };

    private static byte[] Encode(VipsImage image, string contentType, int quality) => contentType switch
    {
        "image/jpeg" => image.JpegsaveBuffer(q: quality, keep: Enums.ForeignKeep.None),
        "image/webp" => image.WebpsaveBuffer(q: quality, keep: Enums.ForeignKeep.None),
        _ => image.PngsaveBuffer(keep: Enums.ForeignKeep.None),
    };
}
