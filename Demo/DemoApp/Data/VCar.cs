using DemoApp.Indexes;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Data;

[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? OwnerFullName { get; set; }
}
