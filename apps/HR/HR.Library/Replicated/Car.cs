using MintPlayer.Spark.Replication.Abstractions;
using System.Drawing;

namespace HR.Replicated;

/// <summary>
/// A read-only copy of Fleet's <c>cars</c> contract (v1, <c>Fleet.Contracts.CarV1</c>). Fleet
/// decides which fields are replicated and generates the ETL transform; this type only has that shape.
/// </summary>
[Replicated(
    SourceModule = "Fleet",
    Contract = "cars",
    SourceCollection = "Cars")]
public class Car
{
    /// <summary>Identifier of the car, replicated read-only from the Fleet app.</summary>
    public string? Id { get; set; }
    /// <summary>Registration plate of the car as maintained in the Fleet app.</summary>
    public string LicensePlate { get; set; } = string.Empty;
    /// <summary>Make and model of the car as maintained in the Fleet app.</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Year the car was built, as maintained in the Fleet app.</summary>
    public int Year { get; set; }
    /// <summary>Exterior colour of the car, as maintained in the Fleet app.</summary>
    public Color? Color { get; set; }
}
