using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Settings;

/// <summary>
/// One setting, declared in code (<c>docs/seo_analyzer_PRD.md</c> X9). The key is never typed by an
/// administrator: the declaration is what makes a key exist, and <see cref="ISparkSettings"/> reads it as
/// the stored value, else <c>IConfiguration[Key]</c>, else <see cref="Default"/>.
/// <code>
/// public static class SeoSettings
/// {
///     public static readonly SparkSetting&lt;bool&gt; IntrusiveProbes = new("Seo:Features:IntrusiveProbes", false, "Run probes that send unusual requests.");
///     public static readonly SparkSetting&lt;int&gt; MaxPages = new("Seo:Limits:MaxPages", 100, "Pages per audit.");
/// }
/// spark.AddSettings(typeof(SeoSettings));
/// </code>
/// </summary>
/// <remarks>
/// ⚠️ Never declare a secret: the stored value is plaintext and every settings administrator sees it.
/// Secrets stay in configuration, read through <c>IConfiguration</c>.
/// </remarks>
/// <typeparam name="T">
/// Any type the configuration binder parses from a string with its <see cref="TypeConverter"/>: <c>bool</c>,
/// the numeric types, <see cref="TimeSpan"/>, <see cref="string"/>, an enum, and their nullable forms.
/// </typeparam>
/// <param name="Key">The configuration key, also the document id <c>Settings/{Key}</c>. Letters, digits, <c>: . _ -</c>.</param>
/// <param name="Default">The value when neither a stored value nor configuration has one.</param>
/// <param name="Description">What the setting does, shown to the administrator.</param>
public sealed record SparkSetting<T>(string Key, T Default, string Description) : ISparkSetting<T>
{
    Type ISparkSetting.ValueType => typeof(T);

    string? ISparkSetting.DefaultText => SparkSettingValues.Format(Default);

    bool ISparkSetting.CanParse(string text) => SparkSettingValues.TryParse<T>(text, out _);
}

/// <summary>A declared setting of type <typeparamref name="T"/>.</summary>
public interface ISparkSetting<out T> : ISparkSetting
{
    /// <summary>The value when neither a stored value nor configuration has one.</summary>
    T Default { get; }
}

/// <summary>The untyped view of a <see cref="SparkSetting{T}"/>, for the seeder and the save validation.</summary>
public interface ISparkSetting
{
    /// <summary>The configuration key, also the document id <c>Settings/{Key}</c>.</summary>
    string Key { get; }

    /// <summary>The declared value type.</summary>
    Type ValueType { get; }

    /// <summary>What the setting does.</summary>
    string Description { get; }

    /// <summary>The default formatted the way a stored or configured value is written (invariant culture).</summary>
    string? DefaultText { get; }

    /// <summary>Whether <paramref name="text"/> parses as the declared type.</summary>
    bool CanParse(string text);
}

/// <summary>
/// Parsing and formatting shared by stored and configured values, so <c>"00:05:00"</c>, <c>"true"</c> or
/// <c>"200"</c> mean the same from either source: the <see cref="TypeConverter"/> and invariant culture the
/// configuration binder uses.
/// </summary>
internal static partial class SparkSettingValues
{
    public static bool TryParse<T>(string text, out T value)
    {
        value = default!;
        try
        {
            if (typeof(T) == typeof(string))
            {
                value = (T)(object)text;
                return true;
            }

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (!converter.CanConvertFrom(typeof(string)) || converter.ConvertFromInvariantString(text) is not T parsed)
                return false;
            value = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    public static string? Format<T>(T value) => value switch
    {
        null => null,
        string s => s,
        IFormattable f when value is not Enum => f.ToString(null, CultureInfo.InvariantCulture),
        _ => TypeDescriptor.GetConverter(typeof(T)).ConvertToInvariantString(value),
    };

    /// <summary>The type as an administrator reads it: <c>Int32</c>, <c>Boolean</c>, <c>TimeSpan?</c>.</summary>
    public static string TypeName(Type type)
        => Nullable.GetUnderlyingType(type) is { } inner ? inner.Name + "?" : type.Name;

    /// <summary>A key that is safe as a document id and a configuration path.</summary>
    public static bool IsValidKey(string key) => !string.IsNullOrEmpty(key) && key.Length <= 200 && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:._-]*$")]
    private static partial Regex KeyPattern();
}
