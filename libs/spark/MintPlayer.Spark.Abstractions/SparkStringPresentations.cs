namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The <c>dataType</c> values that mean "still a string, render it differently".
/// </summary>
/// <remarks>
/// A CLR <c>string</c> property can be a paragraph, a link, or the address of an image, and nothing
/// about the type says which. So these are <b>hand-authored in the model file</b> and the
/// synchronizer preserves them, rather than resetting them to <c>"string"</c> on every run from a
/// CLR shape that cannot know better.
/// <para>
/// <c>MultiLineString</c> worked this way already, as a one-off special case in the synchronizer.
/// This exists because <c>image</c> and <c>url</c> (#327 §9.1) are the same idea, and a second
/// hard-coded name in that condition is how the list starts silently disagreeing with whatever the
/// client actually renders.
/// </para>
/// <para>
/// ⚠️ The preservation is conditional on the property still being a string. Change the property to
/// a <c>DateTime</c> and the override is dropped, because it is then describing something that is
/// no longer true — a stale presentation hint over a changed shape is worse than none.
/// </para>
/// </remarks>
public static class SparkStringPresentations
{
    /// <summary>A string rendered as a textarea rather than a single-line input.</summary>
    public const string MultiLine = "MultiLineString";

    /// <summary>
    /// A string holding an image's address (an <c>https:</c> URL or a <c>data:</c> URI), rendered as the
    /// image itself. The same <c>Image</c> data type a <see cref="SparkImage"/> property gets (#700): the
    /// old lowercase <c>image</c> presentation is normalised to it by the synchronizer.
    /// </summary>
    public const string Image = SparkFileDataTypes.Image;

    /// <summary>A string holding a file's address (an <c>https:</c> URL or a <c>data:</c> URI), offered as a download (#700).</summary>
    public const string BinaryFile = SparkFileDataTypes.BinaryFile;

    /// <summary>A string holding a link, rendered as an anchor.</summary>
    public const string Url = "url";

    /// <summary>Every presentation-only override of a string property.</summary>
    public static readonly string[] All = [MultiLine, Image, BinaryFile, Url];

    /// <summary>
    /// Whether <paramref name="declared"/> is a presentation override that survives a synchronize
    /// over a property whose CLR shape is still <paramref name="derived"/>.
    /// </summary>
    public static bool Preserves(string? declared, string derived)
        => derived == "string"
        && declared is not null
        && All.Contains(declared, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The canonical spelling of a preserved presentation: <c>image</c> → <c>Image</c> (#700), and any
    /// other casing of a known name to its constant. The synchronizer writes this back, so a model file
    /// carrying the pre-#700 <c>image</c> is migrated by the next synchronize.
    /// </summary>
    public static string Canonical(string declared)
        => All.FirstOrDefault(p => string.Equals(p, declared, StringComparison.OrdinalIgnoreCase)) ?? declared;
}
