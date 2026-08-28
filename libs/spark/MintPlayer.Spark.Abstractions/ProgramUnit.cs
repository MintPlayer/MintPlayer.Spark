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

    /// <summary>
    /// What this unit opens. Canonical values (the loader normalizes case and validates the
    /// matching target field is present):
    /// <list type="bullet">
    ///   <item><c>"query"</c> — the query named by <see cref="QueryId"/>.</item>
    ///   <item><c>"persistentObject"</c> — the entity type named by
    ///   <see cref="PersistentObjectId"/>: its default list when <see cref="ObjectId"/> is absent,
    ///   or that specific object's page when present.</item>
    ///   <item><c>"url"</c> — the external address in <see cref="Url"/>.</item>
    /// </list>
    /// </summary>
    public required string Type { get; set; }

    public Guid? QueryId { get; set; }
    public Guid? PersistentObjectId { get; set; }

    /// <summary>
    /// For a <c>persistentObject</c> unit: the id of the specific object to open — the menu entry
    /// becomes a deep link to one page (<c>/po/{type}/{objectId}</c>). For a composed page the id
    /// is any stable string the application chooses; the type's Actions class receives it in
    /// <c>OnComposeAsync</c> and may ignore it. Absent means the type's default list.
    /// </summary>
    public string? ObjectId { get; set; }

    /// <summary>
    /// For a <c>url</c> unit: the external address. Deliberately its own field rather than an
    /// overload of <see cref="ObjectId"/> — one string with two meanings fails the obviousness
    /// test. Rendered as a plain anchor (new tab), never a router link.
    /// </summary>
    public string? Url { get; set; }

    public int Order { get; set; }
    /// <summary>
    /// Optional URL-friendly alias for this program unit's target.
    /// If set, the frontend navigation will use this alias instead of the GUID.
    /// </summary>
    public string? Alias { get; set; }
}
