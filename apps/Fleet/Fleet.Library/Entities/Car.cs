using System.Drawing;
using MintPlayer.Spark.Abstractions;

namespace Fleet.Entities;

/// <summary>
/// Demonstrates <see cref="GenerateIndexAttribute"/>: the <c>Cars_Overview</c> index and its <c>VCar</c>
/// index entity are generated into the <em>Fleet app</em> project, while this entity stays in the lean
/// library. Nothing here references anything generated — that is what keeps the library free of index types,
/// so it can be referenced for replication without dragging them along.
/// </summary>
[GenerateIndex]
public class Car
{
    /// <summary>Unique identifier of this vehicle record, assigned automatically when it is created.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// <see cref="SearchAttribute"/> makes this full-text searchable AND gives it a <c>LicensePlateSort</c>
    /// companion. Both, from one attribute: an analyzed field is tokenized, so ordering on it is meaningless,
    /// and the companion is the repair.
    /// </summary>
    [Search]
    public string LicensePlate { get; set; } = string.Empty;

    /// <summary>Searchable, and the reason the companion exists: model names contain spaces.</summary>
    [Search]
    public string Model { get; set; } = string.Empty;
    /// <summary>Model year of the vehicle, as stated on its registration.</summary>
    public int Year { get; set; }
    /// <summary>Exterior paint colour of the vehicle.</summary>
    public Color? Color { get; set; }
    /// <summary>Colour of the vehicle's interior upholstery and trim.</summary>
    public Color? InteriorColor { get; set; }
    /// <summary>Link to a promotional video of the vehicle; hidden while the vehicle is reported stolen.</summary>
    public string? PromoVideoUrl { get; set; }

    /// <summary>Current fleet status of the vehicle: <c>InUse</c>, <c>OnParking</c>, <c>InMaintenance</c> or <c>Stolen</c>.</summary>
    [LookupReference(typeof(LookupReferences.CarStatus))]
    public string? Status { get; set; }

    /// <summary>Manufacturer brand of the vehicle, chosen from the list of known car brands.</summary>
    [LookupReference(typeof(LookupReferences.CarBrand))]
    public string? Brand { get; set; }

    /// <summary>
    /// Police report reference, demanded once the vehicle is reported stolen.
    /// <para>
    /// Hidden and optional in the model; <c>CarActions.OnRefreshAsync</c> reveals it and makes it
    /// required when <see cref="Status"/> becomes <c>Stolen</c>. That is the whole point of the
    /// sample — the model declares the resting shape, the hook declares how the shape responds.
    /// </para>
    /// <see cref="IgnoreForIndexAttribute"/> because no grid filters or sorts on it, matching
    /// <see cref="Manager"/> and <see cref="CreatedBy"/>.
    /// </summary>
    [IgnoreForIndex]
    public string? PoliceReportNumber { get; set; }

    /// <summary>
    /// Id of the Person (replicated from HR) acting as fleet manager for this vehicle.
    /// Demo field — exercises the inverse-path reference round-trip end-to-end: client
    /// sets the id, round-trip re-fetch resolves the breadcrumb via the Person
    /// replication collection.
    /// </summary>
    /// <para>
    /// <see cref="IgnoreForIndexAttribute"/> keeps it out of the generated index while leaving it a full
    /// member of the model — the opposite trade-off to <see cref="IgnorePropertyAttribute"/> below. A grid
    /// never filters or sorts by it, so indexing it would only cost index size and re-indexing work.
    /// </para>
    [Reference(typeof(Fleet.Replicated.Person))]
    [IgnoreForIndex]
    public string? Manager { get; set; }

    /// <summary>
    /// Free-text description maintained in multiple languages. Exercises the inverse-path
    /// TranslatedString per-language merge behavior — a partial update that carries only
    /// <c>{ en: "…" }</c> must preserve the existing <c>fr</c> / <c>nl</c> entries rather
    /// than overwriting the whole value.
    /// </summary>
    /// <para>
    /// Also demonstrates the <c>TranslatedString</c> fan-out: the generated index cannot sort or search a
    /// dictionary, so it emits one <c>Description_{lang}</c> field per language in
    /// <c>App_Data/culture.json</c>, mapped from <c>Description.Translations["{lang}"]</c>.
    /// </para>
    public TranslatedString? Description { get; set; }

    /// <summary>
    /// User id of the account that created the record. Set on create by CarActions; used
    /// by the row-level auth hook to restrict non-admin callers to their own cars.
    /// Demo field: wouldn't necessarily live on the entity in a production app (could be
    /// a metadata field), but keeping it on the entity is the simplest illustration.
    /// </summary>
    [IgnoreForIndex]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// ETag returned by the external vehicle-registry sync, stored so a later run can skip a
    /// record that hasn't changed upstream.
    /// <para>
    /// Demo field for <see cref="IgnorePropertyAttribute"/>: it is persisted by RavenDB like any
    /// other property, but it is pure infrastructure — no place on a form or in a grid, and a
    /// client must never be able to write it. A get-only property would not work here, because
    /// the sync job has to assign it; that is exactly the gap <c>[IgnoreProperty]</c> fills.
    /// Note that no attribute for it appears in <c>App_Data/Model/Car.json</c>.
    /// </para>
    /// </summary>
    [IgnoreProperty]
    public string? RegistrySyncEtag { get; set; }
}
