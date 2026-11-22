namespace MintPlayer.Spark.Files.Images.SkiaSharp;

public static class SkiaSharpFilesExtensions
{
    /// <summary>
    /// Processes every <c>Image</c> upload with SkiaSharp: the pixel cap from the header, EXIF orientation, a re-encode that
    /// drops all metadata, and the attribute's variants as WebP (PRD §3.6, SP5).
    /// </summary>
    public static FilesBuilder UseSkiaSharpImages(this FilesBuilder files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.UseImageProcessor<SkiaSharpImageProcessor>();
    }
}
