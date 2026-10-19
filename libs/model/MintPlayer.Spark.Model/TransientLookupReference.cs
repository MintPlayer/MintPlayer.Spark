namespace MintPlayer.Spark.Abstractions;

public abstract class TransientLookupReference
{
    public string? Description { get; init; }
    public required TranslatedString Values { get; init; }

    /// <summary>
    /// Controls how the lookup is displayed in the UI.
    /// </summary>
    public abstract ELookupDisplayType DisplayType { get; }

    /// <summary>
    /// A lookup label inline, one plain entry per language, each tagged explicitly so the languages
    /// cannot silently swap: <c>_TS(("en", "Active"), ("fr", "Actif"), ("nl", "Actief"))</c>.
    /// </summary>
    protected static TranslatedString _TS(params ReadOnlySpan<(string Tag, string Value)> texts)
        => TranslatedString.Of(texts);
}

public abstract class TransientLookupReference<TKey> : TransientLookupReference
{
    public required TKey Key { get; init; }
}
