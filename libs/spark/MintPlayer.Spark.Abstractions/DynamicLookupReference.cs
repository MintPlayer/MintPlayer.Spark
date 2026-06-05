namespace MintPlayer.Spark.Abstractions;

public class EmptyValue { }

public class LookupReferenceValue<TValue> where TValue : new()
{
    public required string Key { get; set; }
    public required TranslatedString Values { get; set; }
    public bool IsActive { get; set; } = true;
    public TValue Extra { get; set; } = new();
}

public abstract class DynamicLookupReference : DynamicLookupReference<EmptyValue>
{
}

public abstract class DynamicLookupReference<TValue> where TValue : new()
{
    public string? Id { get; set; }  // e.g., "LookupReferences/CarBrand"
    public required string Name { get; set; }
    public List<LookupReferenceValue<TValue>> Values { get; set; } = new();

    /// <summary>
    /// Controls how the lookup is displayed in the UI.
    /// </summary>
    public abstract ELookupDisplayType DisplayType { get; }
}
