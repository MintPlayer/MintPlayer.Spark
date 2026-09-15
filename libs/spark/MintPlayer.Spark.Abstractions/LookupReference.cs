namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One lookup reference as <c>GET /spark/lookupref/</c> lists it — the name and size, without the
/// values.
/// </summary>
/// <remarks>
/// ⚠️ These three types live in Abstractions rather than beside <c>ILookupReferenceService</c>
/// because they are the <b>wire shape</b> of the lookup-reference endpoints, and the typed client
/// references only Abstractions. A client-side mirror would be a second declaration of one contract,
/// free to drift silently — the failure mode being a field that quietly stops arriving.
/// </remarks>
public class LookupReferenceListItem
{
    public required string Name { get; set; }
    public required bool IsTransient { get; set; }
    public int ValueCount { get; set; }
    public ELookupDisplayType DisplayType { get; set; } = ELookupDisplayType.Dropdown;
}

/// <summary>One lookup reference with its values — <c>GET /spark/lookupref/{name}</c>.</summary>
public class LookupReferenceDto
{
    public required string Name { get; set; }
    public required bool IsTransient { get; set; }
    public ELookupDisplayType DisplayType { get; set; } = ELookupDisplayType.Dropdown;
    public List<LookupReferenceValueDto> Values { get; set; } = new();
}

/// <summary>One value of a lookup reference: its key, its translated labels, and anything extra.</summary>
public class LookupReferenceValueDto
{
    public required string Key { get; set; }
    public required TranslatedString Values { get; set; }
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object>? Extra { get; set; }
}
