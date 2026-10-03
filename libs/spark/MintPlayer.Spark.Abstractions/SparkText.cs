using System.Text;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Resolves the text an App_Data file refers to (#467, D1/D4). Files never embed translated text:
/// an element finds its text under a conventional key (<c>model.Car.attributes.Brand.label</c>), or
/// under an explicit key the file names instead. When no <c>translations.json</c> layer defines the
/// key, the humanized name is shown (<c>CreatedBy</c> → <c>Created By</c>).
/// </summary>
public static class SparkText
{
    /// <summary>
    /// The text for an element: the explicit key when <paramref name="value"/> carries one, else
    /// <paramref name="conventionKey"/>; <paramref name="fallbackName"/> humanized when neither is
    /// translated. Returns <see langword="null"/> only when there is no fallback either.
    /// </summary>
    public static TranslatedString? Resolve(TranslatedString? value, string conventionKey, string? fallbackName)
    {
        RejectInlineText(value, conventionKey);
        var key = value?.Key ?? conventionKey;
        if (Lookup(key) is { } translated)
            return translated;
        return fallbackName is null ? null : TranslatedString.Create(Humanize(fallbackName));
    }

    /// <summary>
    /// The text under <paramref name="key"/>, or <see langword="null"/> when no layer defines it
    /// in any language. Used for optional text such as an attribute's description.
    /// </summary>
    public static TranslatedString? Lookup(string key)
        => SparkTranslations.All.TryGetValue(key, out var ts) && ts.Translations.Count > 0
            ? new TranslatedString { Translations = new Dictionary<string, string>(ts.Translations) }
            : null;

    /// <summary>
    /// D1: an App_Data file holds a key or nothing, never translated text. Text found inline is a
    /// file that was not migrated, so this names the key it belongs under.
    /// </summary>
    public static void RejectInlineText(TranslatedString? value, string conventionKey)
    {
        if (value is null || value.Key is not null || value.Translations.Count == 0) return;
        throw new InvalidOperationException(
            $"Translated text is embedded in an App_Data file where '{conventionKey}' belongs " +
            $"(\"{value.GetDefaultValue()}\"). Move it into translations.json under '{conventionKey}' and " +
            "remove it from the file, or replace it with the translation key it should use.");
    }

    /// <summary><c>CreatedBy</c> → <c>Created By</c>; the last segment of a dotted key.</summary>
    public static string Humanize(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        if (name.Length == 0) return name;

        var result = new StringBuilder(name.Length + 4);
        result.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]) && name[i - 1] != ' ')
                result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }
}
