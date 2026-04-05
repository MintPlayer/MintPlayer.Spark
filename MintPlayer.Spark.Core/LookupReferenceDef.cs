namespace MintPlayer.Spark.Abstractions;

public class LookupReferenceDef
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool IsTransient { get; set; }
    public ELookupDisplayType DisplayType { get; set; } = ELookupDisplayType.Dropdown;
}
