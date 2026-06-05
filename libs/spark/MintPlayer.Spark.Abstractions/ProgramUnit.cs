namespace MintPlayer.Spark.Abstractions;

public sealed class ProgramUnitsConfiguration
{
    public ProgramUnitGroup[] ProgramUnitGroups { get; set; } = [];
}

public sealed class ProgramUnitGroup
{
    public required Guid Id { get; set; }
    public required TranslatedString Name { get; set; }
    public string? Icon { get; set; }
    public int Order { get; set; }
    public ProgramUnit[] ProgramUnits { get; set; } = [];
}

public sealed class ProgramUnit
{
    public required Guid Id { get; set; }
    public required TranslatedString Name { get; set; }
    public string? Icon { get; set; }
    public required string Type { get; set; }
    public Guid? QueryId { get; set; }
    public Guid? PersistentObjectId { get; set; }
    public int Order { get; set; }
    /// <summary>
    /// Optional URL-friendly alias for this program unit's target.
    /// If set, the frontend navigation will use this alias instead of the GUID.
    /// </summary>
    public string? Alias { get; set; }
}
