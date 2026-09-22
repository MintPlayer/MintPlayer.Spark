namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One column's value filter (#431): the values a caller picked, or the values they excluded.
/// </summary>
/// <remarks>
/// <b>Values, not labels.</b> Each entry is the raw stored value — the same thing the distinct
/// endpoint returned as <c>value</c>, never the <c>label</c> beside it. A reference column's values
/// are document ids while its labels are resolved breadcrumb text, and two rows can legitimately
/// render the same text, so matching on labels would be both wrong and ambiguous.
/// <para>
/// <b>Include or exclude, not both.</b> The inverse toggle moves values between the two rather than
/// setting a flag, matching the protocol this follows. A column that somehow arrives with both
/// populated applies both, which narrows — the safe direction.
/// </para>
/// <para>
/// <b><see langword="null"/> is a value.</b> A null entry matches rows where the column has no value,
/// which is a real and selectable distinct ("&lt; none &gt;" in the panel), not an absent filter. An
/// empty or absent array is the absent filter.
/// </para>
/// </remarks>
public sealed class QueryColumnFilter
{
    /// <summary>The column this filter applies to, matched case-insensitively against the attribute name.</summary>
    public required string Name { get; set; }

    /// <summary>Values to keep. Empty or null means "no include filter on this column".</summary>
    public object?[]? Includes { get; set; }

    /// <summary>Values to drop. Empty or null means "no exclude filter on this column".</summary>
    public object?[]? Excludes { get; set; }

    /// <summary>Whether this filter would narrow anything at all.</summary>
    public bool IsEmpty => (Includes is null || Includes.Length == 0)
                        && (Excludes is null || Excludes.Length == 0);
}
