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
    [Reference(typeof(Fleet.Replicated.Person), "GetPeople")]
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
}
