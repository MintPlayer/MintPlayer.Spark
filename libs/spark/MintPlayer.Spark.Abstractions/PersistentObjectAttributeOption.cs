namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One selectable value in an attribute's option list, as replaced by a refresh hook.
/// <para>
/// Deliberately narrower than <c>LookupReferenceValueDto</c>: a refresh replaces what the user may
/// pick, not the definition of the lookup itself, so there is nothing here about transience, display
/// type or activeness. It is also the shape a Reference attribute's options collapse to, which lets
/// the client render both kinds from one list.
/// </para>
/// </summary>
public sealed class PersistentObjectAttributeOption
{
    /// <summary>The value stored when this option is chosen — a lookup key, or a document id.</summary>
    public required string Key { get; set; }

    /// <summary>What the user sees. Falls back to <see cref="Key"/> when absent.</summary>
    public TranslatedString? Label { get; set; }
}
