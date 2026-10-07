namespace MintPlayer.Spark.Abstractions;

/// <summary>The application's navigation menu (<c>App_Data/programUnits.json</c>): groups of menu entries.</summary>
public sealed class ProgramUnitsConfiguration
{
    /// <summary>The menu's groups, matched across layers by <c>id</c>.</summary>
    public ProgramUnitGroup[] ProgramUnitGroups { get; set; } = [];
}

/// <summary>A titled group of menu entries.</summary>
public sealed class ProgramUnitGroup
{
    /// <summary>The group's stable id; its identity across layers.</summary>
    public required Guid Id { get; set; }

    /// <summary>The group's caption.</summary>
    public required TranslatedString Name { get; set; }

    /// <summary>A Bootstrap Icons name, e.g. <c>bi-house</c>.</summary>
    public string? Icon { get; set; }

    /// <summary>The group's position in the menu.</summary>
    public int Order { get; set; }

    /// <summary>The group's entries.</summary>
    public ProgramUnit[] ProgramUnits { get; set; } = [];
}

/// <summary>One menu entry: opens a query, an entity type's list or page, or an external URL.</summary>
public sealed class ProgramUnit
{
    /// <summary>The entry's stable id; its identity across layers.</summary>
    public required Guid Id { get; set; }

    /// <summary>The entry's caption.</summary>
    public required TranslatedString Name { get; set; }

    /// <summary>A Bootstrap Icons name, e.g. <c>bi-house-door</c>.</summary>
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

    /// <summary>For a <c>query</c> unit: the id of the query to open.</summary>
    public Guid? QueryId { get; set; }

    /// <summary>For a <c>persistentObject</c> unit: the id of the entity type to open.</summary>
    public Guid? PersistentObjectId { get; set; }

    /// <summary>
    /// For a <c>persistentObject</c> unit: the id of the specific object to open — the menu entry
    /// becomes a deep link to one page (<c>/po/{type}/{objectId}</c>). For a virtual type's
    /// composed page the id is any stable string the application chooses; the type's Actions
    /// class receives it as <c>OnLoadAsync</c>'s id argument and may ignore it. Absent means the
    /// type's default list.
    /// </summary>
    public string? ObjectId { get; set; }

    /// <summary>
    /// For a <c>url</c> unit: the external address. Deliberately its own field rather than an
    /// overload of <see cref="ObjectId"/> — one string with two meanings fails the obviousness
    /// test. Rendered as a plain anchor (new tab), never a router link.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The entry's position within its group.</summary>
    public int Order { get; set; }
    /// <summary>
    /// Optional URL-friendly alias for this program unit's target.
    /// If set, the frontend navigation will use this alias instead of the GUID.
    /// </summary>
    public string? Alias { get; set; }
}
