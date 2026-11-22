namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The two file data types (#700). Core owns them; the <c>MintPlayer.Spark.Files</c> plugin stores,
/// scans and serves the bytes. A library cannot add data types of its own.
/// </summary>
/// <remarks>
/// The data type says what the value <em>is</em>; the CLR property type says where it comes from:
/// <list type="bullet">
/// <item><see cref="SparkFile"/>/<see cref="SparkImage"/> (or a list of them): a stored upload.</item>
/// <item><c>string</c>: an <c>https:</c> URL or a <c>data:</c> URI, displayed but never re-uploaded. The data
/// type is then hand-authored in the model file and preserved like any other string presentation
/// (<see cref="SparkStringPresentations"/>).</item>
/// </list>
/// </remarks>
public static class SparkFileDataTypes
{
    /// <summary>Any file. Its accept list is empty, and so refuses everything, until the model declares one.</summary>
    public const string BinaryFile = "BinaryFile";

    /// <summary>A raster image (PNG, JPEG, GIF, WebP by default). SVG is never an Image.</summary>
    public const string Image = "Image";

    /// <summary>Whether <paramref name="dataType"/> is one of the file data types (ordinal).</summary>
    public static bool IsFile(string? dataType) => dataType is BinaryFile or Image;
}
