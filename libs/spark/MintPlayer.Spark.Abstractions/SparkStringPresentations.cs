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

    /// <summary>A string holding an image URL, rendered as the image itself.</summary>
    public const string Image = "image";

    /// <summary>A string holding a link, rendered as an anchor.</summary>
    public const string Url = "url";

    /// <summary>Every presentation-only override of a string property.</summary>
    public static readonly string[] All = [MultiLine, Image, Url];

    /// <summary>
    /// Whether <paramref name="declared"/> is a presentation override that survives a synchronize
    /// over a property whose CLR shape is still <paramref name="derived"/>.
    /// </summary>
    public static bool Preserves(string? declared, string derived)
        => derived == "string"
        && declared is not null
        && All.Contains(declared, StringComparer.OrdinalIgnoreCase);
}
