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
    /// User id of the account that created the record. Set on create by CarActions; used
    /// by the row-level auth hook to restrict non-admin callers to their own cars.
    /// Demo field: wouldn't necessarily live on the entity in a production app (could be
    /// a metadata field), but keeping it on the entity is the simplest illustration.
    /// </summary>
    public string? CreatedBy { get; set; }
}
