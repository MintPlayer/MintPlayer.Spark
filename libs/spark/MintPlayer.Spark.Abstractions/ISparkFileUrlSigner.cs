namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Gives each stored file in a read response the <c>url</c> it is fetched from (#700 D15, spike SP1). Core defines it; the
/// <c>MintPlayer.Spark.Files</c> plugin implements it. Without the plugin no file carries a URL.
/// </summary>
/// <remarks>
/// Core calls it from the serializer, beside the boundary net: only for a persistent object being written into a response
/// for the current request, after the net checked it, and only for the values of attributes that survived presentation. So
/// no read path can ask for the URL of a file in an attribute the caller may not read. With no request (a job, a test
/// serializing for itself) nothing is signed.
/// </remarks>
public interface ISparkFileUrlSigner
{
    /// <summary>The URL of <paramref name="file"/>, or of its variant, for the current caller; null when it is not served.</summary>
    string? Sign(SparkFileLocation file);
}

/// <summary>Where a <see cref="SparkFile"/> in a read response belongs.</summary>
/// <param name="ObjectTypeId">The entity type of the object holding the file.</param>
/// <param name="EntityId">The document holding the file (for a file inside an AsDetail row, the row's parent).</param>
/// <param name="Attribute">The attribute holding it, or null when it sits inside an AsDetail row.</param>
/// <param name="File">The stored value.</param>
/// <param name="Variant">The variant asked for (an image's <c>thumb</c>, <c>medium</c>, …), or null for the file itself.</param>
public sealed record SparkFileLocation(Guid ObjectTypeId, string EntityId, string? Attribute, SparkFile File, string? Variant);
