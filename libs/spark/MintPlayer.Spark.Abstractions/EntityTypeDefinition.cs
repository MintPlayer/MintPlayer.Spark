namespace MintPlayer.Spark.Abstractions;

public sealed class EntityTypeDefinition
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Description { get; set; }
    /// <summary>
    /// The CLR type this definition maps to — the anchor of the entity pipeline (load, query,
    /// save, row security). <see langword="null"/> for a JSON-only virtual type: a page that
    /// exists in the model but not in the database, served exclusively through
    /// <c>OnLoadAsync(id, parent)</c> on a <c>{Name}Actions</c> class (resolved by name, and
    /// scaffolding its object via <c>IManager.GetPersistentObject</c> instead of loading one).
    /// Everything document-shaped 404s for such a type.
    /// </summary>
    public string? ClrType { get; set; }
    /// <summary>
    /// Optional URL-friendly alias for this entity type.
    /// Used as an alternative to the GUID in URLs (e.g., /po/car instead of /po/{guid}).
    /// If not set, auto-generated from Name by lowercasing.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Whether the current caller may open a single object of this type — the <c>Read</c> right,
    /// answered per request rather than stored in the model.
    /// </summary>
    /// <remarks>
    /// The catalogue is gated on <c>Query</c>, deliberately: it is a list of things you may LIST.
    /// The client also uses it to decide whether a reference renders as a link, and that is a
    /// different question — a type granted Query but not Read produced a clickable reference that
    /// refused on arrival, while the grid's own first column correctly gated on Read. Carrying the
    /// answer here makes both agree without the client asking per type.
    /// <para>
    /// Not persisted: <c>null</c> in a model file, filled in on the way out. It is per-caller, so
    /// storing it would be meaningless and misleading.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanRead { get; set; }
    /// <summary>
    /// The definitions of this type's AsDetail row types, so a client can render their columns
    /// without resolving them from the <c>Query</c>-gated catalogue.
    /// </summary>
    /// <remarks>
    /// An AsDetail row type frequently has no rights of its own — a row is edited through its
    /// parent, so nobody grants <c>Query/{RowType}</c>. The client resolved the row type out of the
    /// catalogue, which is <c>Query</c>-scoped, so those types were absent and the table rendered
    /// with <b>no columns at all</b>: headers gone, rows reduced to an action cell (#385).
    /// <para>
    /// Gated on the <b>parent's</b> right, which is the same gate that already ships the row
    /// schema: <c>EntityMapper.ScaffoldFrom</c> stamps every row in <c>attr.Objects</c> with its
    /// type's attributes — labels, data types and validation rules included — with no permission
    /// check on the row type. This carries the same information for the case that path cannot
    /// reach, an empty collection.
    /// </para>
    /// <para>
    /// ⚠️ Pruned, not copied wholesale. <c>QueryType</c>, <c>IndexName</c>, <c>Queries</c> and
    /// <c>Alias</c> are cleared on the embedded copy: those are the projection-and-query surface
    /// <c>PRD-SecurityAudit.md</c> names as worth withholding, and no client needs them to draw a
    /// detail table.
    /// </para>
    /// <para>
    /// Not persisted: <c>null</c> in a model file, filled in on the way out, per-caller — exactly
    /// as <see cref="CanRead"/>. <c>ModelSynchronizer</c> writes definitions back to disk, so a
    /// property that serialized by default would round-trip nested copies into every model file,
    /// permanently.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public EntityTypeDefinition[]? DetailTypes { get; set; }
    /// <summary>
    /// The CLR type name of the projection type used for RavenDB index queries.
    /// Set when a projection class has [FromIndex] attribute linking to an index for this entity.
    /// Example: "Demo.Data.VCar"
    /// </summary>
    public string? QueryType { get; set; }
    /// <summary>
    /// The name of the entity's default RavenDB index — the [DefaultIndex]-elected (or sole)
    /// projection-bearing index, written by the synchronizer. Load-bearing at runtime: the PO-list
    /// path queries through it, and a query without its own indexName falls back to it.
    /// Example: "Cars_Overview"
    /// </summary>
    public string? IndexName { get; set; }
    /// <summary>
    /// Breadcrumb template: literal text plus <c>{AttributeName}</c> placeholders. A scalar
    /// placeholder renders its value; a reference placeholder renders the referenced entity's
    /// breadcrumb (resolved recursively). Authored against the collection type's property names.
    /// Example: <c>"{ParkedCar} ({Coordinates})"</c>. Source of truth: <c>[Breadcrumb]</c> attribute,
    /// else a value preserved in the model JSON, else a synthesized default.
    /// </summary>
    public string? Breadcrumb { get; set; }
    /// <summary>
    /// Whether every <c>{…}</c> placeholder in <see cref="Breadcrumb"/> is present on the
    /// <see cref="QueryType"/> projection. <c>null</c> means satisfiable / not applicable
    /// (no projection); <c>false</c> means the breadcrumb needs the collection document
    /// (the list path must batch-load collection docs to render it). Only persisted when
    /// <c>false</c>, so satisfiable types add no JSON noise.
    /// </summary>
    public bool? BreadcrumbProjectionSatisfiable { get; set; }
    /// <summary>
    /// Whether adding or removing a row of this type in an <c>AsDetail</c> grid asks the server
    /// first. <see langword="null"/> or <see langword="false"/> — the default — keeps the purely
    /// client-side behaviour: New pushes a blank row, Delete splices it out, and the parent's save
    /// is the first the server hears of either.
    /// </summary>
    /// <remarks>
    /// Set it on the <b>row type's own</b> model file, not on the parent's attribute: the type that
    /// owns the hooks owns the decision, and one setting then governs every grid the type appears
    /// in.
    /// <para>
    /// Turning it on buys two distinct things, and the second is the one that matters. The first is
    /// the hooks — <c>OnNewAsync</c> to default a new row, and the delete hook to refuse or react.
    /// The second is that <c>New/{Type}</c> and <c>Delete/{Type}</c> become answerable questions:
    /// the endpoints enforce them, so a caller poking the API directly is refused rather than
    /// silently obeyed.
    /// </para>
    /// <para>
    /// ⚠️ For an embedded type the round-trip writes nothing. The row lives inside its parent's
    /// document, so the hook is ceremony — a place to validate, default, veto or audit — and the
    /// row appears or disappears for real only when the parent is saved. An implementation that
    /// touches the database from these hooks is writing outside the parent's unit of work.
    /// </para>
    /// <para>
    /// ⚠️ The endpoints are not, by themselves, the enforcement point. A caller who skips them and
    /// saves the parent with a row added or removed reaches the same end state, so the parent's
    /// save must apply the same rights by comparing the incoming collection against the stored one.
    /// </para>
    /// <para>
    /// That last point is also what keeps this flag out of the model hash, which covers only
    /// <c>name</c>, <c>clrType</c>, <c>alias</c>, <c>queryType</c> and <c>indexName</c>. Save-time
    /// enforcement must <b>not</b> read this flag: it applies to every embedded collection whether
    /// or not the round-trip is on. Were the rights check conditional on it, editing this one
    /// unhashed line on a deployed model would switch the check off, and the flag would belong in
    /// the hash instead.
    /// </para>
    /// </remarks>
    public bool? ServerSideRowLifecycle { get; set; }

    public AttributeTab[] Tabs { get; set; } = [];
    public AttributeGroup[] Groups { get; set; } = [];
    public EntityAttributeDefinition[] Attributes { get; set; } = [];
    /// <summary>
    /// Query aliases or IDs to display as related query tables on the detail page.
    /// Each entry references a SparkQuery that accepts parent context.
    /// </summary>
    public string[] Queries { get; set; } = [];

    /// <summary>
    /// A shallow copy, for a request that must present this definition differently without
    /// changing it for everyone.
    /// </summary>
    /// <remarks>
    /// <c>ModelLoader</c> is a singleton and hands every request references into one mutable
    /// graph, so mutating a definition in place is a permanent, process-wide,
    /// first-caller-wins change. This exists so a per-request projection is one obvious call
    /// rather than a hand-written field-by-field copy that silently drops the next property
    /// somebody adds.
    /// <para>
    /// Shallow: <c>Attributes</c>, <c>Tabs</c> and <c>Groups</c> are shared with the original.
    /// Replace an array wholesale on the copy; never mutate one through it.
    /// </para>
    /// </remarks>
    public EntityTypeDefinition ShallowCopy() => (EntityTypeDefinition)MemberwiseClone();
}

public sealed class EntityAttributeDefinition
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    /// <summary>
    /// Help text for the person filling in or reading this attribute, rendered by the client as an
    /// [i] tooltip beside the label (#348). Presentational: not part of the model hash, so it can be
    /// authored and translated by hand. The English text is seeded on synchronize from a
    /// <c>[Description]</c> attribute or the property's <c>///</c> summary when either exists; the
    /// other languages are author-owned. Unlike <see cref="EntityTypeDefinition.Description"/>,
    /// which is the page heading, this is explanatory prose.
    /// </summary>
    public TranslatedString? Description { get; set; }
    public string DataType { get; set; } = "string";
    public bool IsRequired { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public int Order { get; set; }
    public string? Query { get; set; }
    /// <summary>
    /// For reference attributes, specifies the target entity type's CLR type name.
    /// </summary>
    public string? ReferenceType { get; set; }
    /// <summary>
    /// For AsDetail attributes, specifies the nested entity type's CLR type name.
    /// </summary>
    public string? AsDetailType { get; set; }
    /// <summary>
    /// When true, the attribute represents an array/collection of AsDetail objects (e.g., CarreerJob[]).
    /// When false (default), the attribute represents a single AsDetail object (e.g., Address?).
    /// </summary>
    public bool IsArray { get; set; }
    /// <summary>
    /// For array AsDetail attributes, controls how items are edited.
    /// "modal" (default) opens a dialog; "inline" edits directly in the table row.
    /// </summary>
    public string? EditMode { get; set; }
    /// <summary>
    /// For Reference attributes, controls how the value is picked in the PO-edit UI.
    /// <see cref="EReferenceDisplayType.Modal"/> renders a readonly textbox with a "…" button that
    /// opens a searchable modal grid; <see cref="EReferenceDisplayType.Dropdown"/> (or null/default)
    /// renders a <c>&lt;bs-select&gt;</c>. Hand-set in the model JSON and preserved across synchronize
    /// (like <see cref="EditMode"/>). Only meaningful when <see cref="DataType"/> is "Reference".
    /// </summary>
    public EReferenceDisplayType? ReferenceDisplayType { get; set; }
    /// <summary>
    /// For array AsDetail attributes, when true the rows can be drag-reordered in the
    /// PO-edit UI (order = array position). Set by <c>[Sortable]</c> via the synchronizer.
    /// Null/absent for non-sortable attributes. Only meaningful when
    /// <see cref="DataType"/> is "AsDetail" and <see cref="IsArray"/> is true.
    /// </summary>
    public bool? IsSortable { get; set; }
    /// <summary>
    /// When true, changing this attribute's value asks the server to reshape the object: the client
    /// posts the in-progress object to <c>/spark/po/{objectTypeId}/refresh</c> and the entity's
    /// actions class receives <c>OnRefreshAsync</c>, which may toggle <see cref="IsRequired"/>,
    /// <see cref="IsReadOnly"/> and <see cref="IsVisible"/>, rewrite <see cref="Rules"/>, replace an
    /// attribute's selectable options, or set dependent values.
    /// <para>
    /// Hand-set in the model JSON and preserved across synchronize (like <see cref="EditMode"/> and
    /// <see cref="ReferenceDisplayType"/>). Deliberately absent from
    /// <see cref="PersistentObjectAttribute"/>: the flag never travels on the wire object, so a
    /// client cannot claim a trigger the model did not declare.
    /// </para>
    /// </summary>
    public bool? TriggersRefresh { get; set; }
    /// <summary>
    /// For LookupReference attributes, specifies the lookup reference type name.
    /// Example: "CarStatus", "CarBrand"
    /// </summary>
    public string? LookupReferenceType { get; set; }
    /// <summary>
    /// When false, this attribute exists only in the projection type (e.g., computed by index).
    /// Not present in the collection entity. Used for list views only.
    /// </summary>
    public bool? InCollectionType { get; set; }
    /// <summary>
    /// When false, this attribute exists only in the collection type (not projected by the index).
    /// Used for detail/edit views only.
    /// </summary>
    public bool? InQueryType { get; set; }
    /// <summary>
    /// Controls on which pages the attribute should be displayed.
    /// Query = shown in list views, PersistentObject = shown in detail/edit views.
    /// Default is both (Query | PersistentObject).
    /// </summary>
    public EShowedOn ShowedOn { get; set; } = EShowedOn.Query | EShowedOn.PersistentObject;
    public ValidationRule[] Rules { get; set; } = [];
    /// <summary>
    /// References an AttributeGroup.Id to assign this attribute to a group.
    /// When null, the attribute is placed in a default (ungrouped) section.
    /// </summary>
    public Guid? Group { get; set; }
    /// <summary>
    /// Number of grid columns this attribute spans within a tab's column layout.
    /// Defaults to 1 when not specified.
    /// </summary>
    public int? ColumnSpan { get; set; }
    /// <summary>
    /// Optional renderer name that tells the frontend which custom component to use
    /// for read-only display (detail page and query list).
    /// Example: "video-player", "color-swatch", "markdown"
    /// </summary>
    public string? Renderer { get; set; }
    /// <summary>
    /// Optional configuration for the renderer (passed as-is to the frontend component).
    /// Example: { "width": 480, "height": 270, "autoplay": false }
    /// </summary>
    public Dictionary<string, object>? RendererOptions { get; set; }
}

public sealed class AttributeTab
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    public int Order { get; set; }
    /// <summary>
    /// Number of columns for the grid layout within this tab.
    /// </summary>
    public int? ColumnCount { get; set; }
}

public sealed class AttributeGroup
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    /// <summary>
    /// References an AttributeTab.Id to assign this group to a tab.
    /// When null, the group is placed on the first/default tab.
    /// </summary>
    public Guid? Tab { get; set; }
    public int Order { get; set; }
}
