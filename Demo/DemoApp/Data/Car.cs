using DemoApp.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Data;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }

    [LookupReference(typeof(CarStatus))]
    public string? Status { get; set; }

    [LookupReference(typeof(CarBrand))]
    public string? Brand { get; set; }

    [Reference(typeof(Company), "GetCompanies")]
    public string? Owner { get; set; }
}
