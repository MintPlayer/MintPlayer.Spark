namespace MintPlayer.Spark.Abstractions;

public abstract class TransientLookupReference
{
    public required string Key { get; init; }
    public string? Description { get; init; }
    public required TranslatedString Values { get; init; }

    /// <summary>
    /// Controls how the lookup is displayed in the UI.
    /// </summary>
    public abstract ELookupDisplayType DisplayType { get; }

    /// <summary>
    /// Helper method to create TranslatedString inline
    /// </summary>
    protected static TranslatedString _TS(string en, string? fr = null, string? nl = null)
        => TranslatedString.Create(en, fr, nl);
}
