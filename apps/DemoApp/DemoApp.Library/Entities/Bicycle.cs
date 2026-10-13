namespace DemoApp.Library.Entities;

/// <summary>
/// A bicycle in the company fleet. Bicycles and motorcycles share one overview index, <c>TwoWheelers_Overview</c>, a
/// multi-map that is the default index of both (<c>docs/default_index_PRD.md</c>, D4).
/// </summary>
public class Bicycle
{
    /// <summary>Unique identifier of this bicycle, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }
    /// <summary>Manufacturer of the bicycle, for example <c>Gazelle</c>.</summary>
    public string Brand { get; set; } = string.Empty;
    /// <summary>Model name of the bicycle.</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Frame size in centimetres.</summary>
    public int FrameSize { get; set; }
}
