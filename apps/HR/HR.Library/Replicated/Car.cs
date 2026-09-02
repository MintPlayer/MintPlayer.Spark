using MintPlayer.Spark.Replication.Abstractions;
using System.Drawing;

namespace HR.Replicated;

/// <summary>
/// A read-only copy of Cars from the Fleet module.
/// The ETL script defines which fields are replicated.
/// </summary>
[Replicated(
    SourceModule = "Fleet",
    SourceCollection = "Cars",
    EtlScript = """
        loadToCars({
            LicensePlate: this.LicensePlate,
            Model: this.Model,
            Year: this.Year,
            Color: this.Color,
            '@metadata': {
                '@collection': 'Cars'
            }
        });
    """)]
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
