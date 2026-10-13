using MintPlayer.Spark.Abstractions;

namespace DemoApp.Indexes;

/// <summary>
/// The view of <see cref="TwoWheelers_Overview"/>: one row shape for bicycles and motorcycles alike.
/// <c>Details</c> is computed per map (frame size or engine capacity), so it exists only on the list.
/// </summary>
[FromIndex(typeof(TwoWheelers_Overview))]
public partial class VTwoWheeler
{
    public string? Id { get; set; }
    [Search]
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Details { get; set; }
}
