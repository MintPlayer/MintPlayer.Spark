using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// The <c>url</c> each file in a read response carries (#700 D15). Core asks only for files of attributes that survived
/// presentation for this caller.
/// <list type="bullet">
/// <item>An image and its variants, and any file inside an AsDetail row, embed a short-lived signed URL, so an
/// <c>&lt;img&gt;</c> needs no further request.</item>
/// <item>A <c>BinaryFile</c> of a top-level attribute embeds the checked download URL instead: opening it runs the read
/// path again, so it never expires and reflects a revoked right at once.</item>
/// </list>
/// A file that is not clean gets no URL.
/// </summary>
[Register(typeof(ISparkFileUrlSigner), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class SparkFileUrlSigner : ISparkFileUrlSigner
{
    [Inject] private readonly FileDownloadService downloads;

    public string? Sign(SparkFileLocation file)
    {
        if (!FileDownloadService.IsServed(file.File, file.Variant))
            return null;
        if (file.Attribute is not null && file.File is not SparkImage)
            return downloads.DownloadUrl(file);
        return downloads.EmbeddedUrl(file.EntityId, file.File, file.Variant);
    }
}
