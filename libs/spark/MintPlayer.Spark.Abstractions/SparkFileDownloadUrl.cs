namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The checked download URL of a stored file (#700 D15, M11): <c>/spark/files/download?type=&amp;id=&amp;attribute=&amp;file=[&amp;variant=]</c>.
/// It never expires and runs the object's read path on every request, so it can be handed out by an app's own endpoint (a
/// profile picture in a custom API response) without that endpoint deciding who may see the file: the download answers 404,
/// or the uniform 401 to an anonymous caller, to anyone the read path refuses.
/// </summary>
/// <remarks>
/// A read response embeds a short-lived signed URL instead (<see cref="ISparkFileUrlSigner"/>), which skips the second read.
/// Use this one where nothing embeds a URL: it costs a read per request and is always current.
/// </remarks>
public static class SparkFileDownloadUrl
{
    /// <summary>The endpoint's path, under the app's path base.</summary>
    public const string Path = "/spark/files/download";

    /// <summary>
    /// The URL of the file <paramref name="location"/> names, relative to the app's root (<paramref name="pathBase"/> in front),
    /// for a file in a top-level attribute (<see cref="SparkFileLocation.Attribute"/>) or <c>Parent.Field</c> of an AsDetail row.
    /// </summary>
    public static string For(SparkFileLocation location, string? pathBase = null)
        => (pathBase ?? string.Empty) + Path
            + "?type=" + location.ObjectTypeId.ToString("D")
            + "&id=" + Uri.EscapeDataString(location.EntityId)
            + "&attribute=" + Uri.EscapeDataString(location.Attribute ?? string.Empty)
            + "&file=" + Uri.EscapeDataString(location.File.FileId)
            + (location.Variant is { } variant ? "&variant=" + Uri.EscapeDataString(variant) : string.Empty);
}
