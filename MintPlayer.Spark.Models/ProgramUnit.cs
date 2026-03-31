namespace MintPlayer.Spark.Abstractions;

public class ProgramUnitsConfiguration
{
    public ProgramUnitGroup[] ProgramUnitGroups { get; set; } = [];
}

public class ProgramUnitGroup
{
    public Guid Id { get; set; }
    public TranslatedString? Name { get; set; }
    public string? Icon { get; set; }
    public int Order { get; set; }
    public ProgramUnit[] ProgramUnits { get; set; } = [];
}

public class ProgramUnit
{
    public Guid Id { get; set; }
    public TranslatedString? Name { get; set; }
    public string? Icon { get; set; }
    public string Type { get; set; } = "query";
    public Guid? QueryId { get; set; }
    public Guid? PersistentObjectId { get; set; }
    public int Order { get; set; }
    /// <summary>
    /// Optional URL-friendly alias for this program unit's target.
    /// If set, the frontend navigation will use this alias instead of the GUID.
    /// </summary>
    public string? Alias { get; set; }
    /// <summary>
    /// Optional back-reference to the parent group's ID.
    /// Set by loaders that flatten the hierarchy (e.g., SparkEditor).
    /// </summary>
    public Guid? GroupId { get; set; }
}
