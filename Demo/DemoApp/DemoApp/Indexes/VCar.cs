using DemoApp.Library.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Indexes;

[FromIndex(typeof(Cars_Overview))]
public partial class VCar
{
    public string? Id { get; set; }
    [Search]
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    [Search]
    public string? OwnerFullName { get; set; }

    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }

}
