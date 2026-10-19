namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// A lookup whose values are declared in code. Declare one with <see cref="SparkLookupAttribute"/> on an
/// enum: the generated class's labels are <c>translations.json</c> keys (<c>lookup.{Enum}.{Member}</c>),
/// which the lookup endpoint resolves (commerce X20 item 5, G1). A hand-written subclass gives its items
/// <see cref="TranslatedString.FromKey"/> labels the same way, never inline text.
/// </summary>
public abstract class TransientLookupReference
{
    public string? Description { get; init; }

    /// <summary>The label: a <c>translations.json</c> key (<see cref="TranslatedString.FromKey"/>), resolved when served.</summary>
    public required TranslatedString Values { get; init; }

    /// <summary>
    /// Controls how the lookup is displayed in the UI.
    /// </summary>
    public abstract ELookupDisplayType DisplayType { get; }
}

public abstract class TransientLookupReference<TKey> : TransientLookupReference
{
    public required TKey Key { get; init; }
}
