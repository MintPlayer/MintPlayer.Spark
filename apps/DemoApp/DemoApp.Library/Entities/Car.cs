using DemoApp.Library.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Library.Entities;

public class Car
{
    /// <summary>Unique identifier of this car, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }
    /// <summary>Registration number shown on the car's licence plate.</summary>
    public string LicensePlate { get; set; } = string.Empty;
    /// <summary>Model name of the car, for example <c>Golf</c> or <c>Model 3</c>.</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Model year in which the car was built.</summary>
    public int Year { get; set; }
    /// <summary>Exterior paint colour of the car.</summary>
    public string? Color { get; set; }

    /// <summary>Current state of the car: in use, on the parking lot, in maintenance or stolen.</summary>
    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }

    /// <summary>Manufacturer of the car, picked from the list of known brands.</summary>
    [LookupReference(typeof(CarBrand))]
    public string? Brand { get; set; }

    /// <summary>Company that owns this car, picked from the companies list.</summary>
    [Reference(typeof(Company), "GetCompanies")]
    public string? Owner { get; set; }
}
