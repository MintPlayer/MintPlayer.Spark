namespace MintPlayer.Spark.Files.Images.NetVips;

public static class NetVipsFilesExtensions
{
    /// <summary>
    /// Processes every <c>Image</c> upload with libvips: the pixel cap from the header, EXIF orientation, a re-encode that
    /// drops all metadata, and the attribute's variants as WebP (PRD §3.6, SP5). The libvips natives are LGPL-2.1.
    /// </summary>
    public static FilesBuilder UseNetVipsImages(this FilesBuilder files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.UseImageProcessor<NetVipsImageProcessor>();
    }
}
