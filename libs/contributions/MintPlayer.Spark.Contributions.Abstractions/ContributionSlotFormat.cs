using System.Globalization;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The one text form of a slot value (PRD T2). The generated <c>GetId</c> methods, the generated row
/// <c>Key</c> and the runtime's slot validation all go through these, so an id built at compile time
/// and one checked at run time can never disagree.
/// </summary>
/// <remarks>
/// <para>
/// Every form is culture-independent: integral types use <see cref="CultureInfo.InvariantCulture"/>,
/// an enum is its <em>name</em>, a <see cref="Guid"/> is its 32 hex digits (format <c>N</c> — the
/// hyphenated <c>D</c> form is 36 characters, over the 32 the id segment allows), and a
/// <see cref="bool"/> is <c>true</c>/<c>false</c> in lower case.
/// </para>
/// <para>
/// <see langword="null"/> formats as the empty string, which <see cref="IsValid"/> refuses: a nullable
/// slot type is allowed, a null slot value is not. Formatting never throws, because the row key is
/// read for every row on every save, including rows the save is about to refuse.
/// </para>
/// </remarks>
public static class ContributionSlotFormat
{
    /// <summary>The longest formatted slot value an id segment allows.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Whether <paramref name="text"/> is a valid id segment: 1 to 32 characters, each an ASCII letter,
    /// digit or <c>-</c> (<c>^[A-Za-z0-9-]{1,32}$</c>). A <c>/</c> would shift the id structure.
    /// </summary>
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
            return false;

        foreach (var c in text)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
            if (!ok)
                return false;
        }

        return true;
    }

    /// <summary>A string slot, as is; <see langword="null"/> becomes empty.</summary>
    public static string Format(string? value) => value ?? string.Empty;

    /// <summary>An enum slot, by name. A value with no name formats as its number.</summary>
    public static string FormatEnum<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString();

    /// <summary>A nullable enum slot, by name; <see langword="null"/> becomes empty.</summary>
    public static string FormatEnum<TEnum>(TEnum? value) where TEnum : struct, Enum => value?.ToString() ?? string.Empty;

    /// <summary>A <see cref="Guid"/> slot, as 32 hex digits (format <c>N</c>).</summary>
    public static string Format(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>A nullable <see cref="Guid"/> slot; <see langword="null"/> becomes empty.</summary>
    public static string Format(Guid? value) => value is { } v ? Format(v) : string.Empty;

    /// <summary>A <see cref="bool"/> slot, as <c>true</c> or <c>false</c>.</summary>
    public static string Format(bool value) => value ? "true" : "false";

    /// <summary>A nullable <see cref="bool"/> slot; <see langword="null"/> becomes empty.</summary>
    public static string Format(bool? value) => value is { } v ? Format(v) : string.Empty;

    /// <summary>An integral slot, in the invariant culture.</summary>
    public static string Format(byte value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(sbyte value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(short value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(ushort value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(uint value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc cref="Format(byte)"/>
    public static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A nullable integral slot; <see langword="null"/> becomes empty.</summary>
    public static string Format(byte? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(sbyte? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(short? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(ushort? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(int? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(uint? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(long? value) => value is { } v ? Format(v) : string.Empty;
    /// <inheritdoc cref="Format(byte?)"/>
    public static string Format(ulong? value) => value is { } v ? Format(v) : string.Empty;
}
