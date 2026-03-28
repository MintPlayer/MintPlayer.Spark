namespace SparkEditor.Entities;

public class ProgramUnitDef
{
    public string? Id { get; set; }
    public string? Name { get; set; }  // TranslatedString JSON
    public string? Icon { get; set; }
    public string Type { get; set; } = "query";
    public string? QueryId { get; set; }
    public string? PersistentObjectId { get; set; }
    public int Order { get; set; }
    public string? Alias { get; set; }
    public string? GroupId { get; set; }
}
