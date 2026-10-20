using MintPlayer.Spark.Replication.Abstractions;
using System.Drawing;

namespace HR.Replicated;

/// <summary>
/// A read-only copy of Fleet's cars (warehouse §4.2.1): this class is the only declaration of what HR reads. The
/// replication generator renders the manifest, the script and the hash from it; Fleet re-renders and approves them.
/// An edit here is written back to Fleet (per-property compare-and-set) and arrives back with Fleet's push.
/// </summary>
[Replicated(SourceCollection = "Cars", SourceModule = "Fleet", WriteBack = true)]
public partial class Car
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
