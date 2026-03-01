using MintPlayer.Spark.Abstractions;

namespace Fleet.Entities;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }
    public string? InteriorColor { get; set; }

    [LookupReference(typeof(LookupReferences.CarStatus))]
    public string? Status { get; set; }

    [LookupReference(typeof(LookupReferences.CarBrand))]
    public string? Brand { get; set; }
}
