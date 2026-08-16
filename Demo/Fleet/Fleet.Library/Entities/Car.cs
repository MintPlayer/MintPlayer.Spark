using System.Drawing;
using MintPlayer.Spark.Abstractions;

namespace Fleet.Entities;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public Color? Color { get; set; }
    public Color? InteriorColor { get; set; }
    public string? PromoVideoUrl { get; set; }

    [LookupReference(typeof(LookupReferences.CarStatus))]
    public string? Status { get; set; }

    [LookupReference(typeof(LookupReferences.CarBrand))]
    public string? Brand { get; set; }

    /// <summary>
    /// Id of the Person (replicated from HR) acting as fleet manager for this vehicle.
    /// Demo field — exercises the inverse-path reference round-trip end-to-end: client
    /// sets the id, round-trip re-fetch resolves the breadcrumb via the Person
    /// replication collection.
    /// </summary>
    [Reference(typeof(Fleet.Replicated.Person))]
    public string? Manager { get; set; }

    /// <summary>
    /// Free-text description maintained in multiple languages. Exercises the inverse-path
    /// TranslatedString per-language merge behavior — a partial update that carries only
    /// <c>{ en: "…" }</c> must preserve the existing <c>fr</c> / <c>nl</c> entries rather
    /// than overwriting the whole value.
    /// </summary>
    public TranslatedString? Description { get; set; }

    /// <summary>
    /// User id of the account that created the record. Set on create by CarActions; used
    /// by the row-level auth hook to restrict non-admin callers to their own cars.
    /// Demo field: wouldn't necessarily live on the entity in a production app (could be
    /// a metadata field), but keeping it on the entity is the simplest illustration.
    /// </summary>
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
