namespace DemoApp.Library.Entities;

/// <summary>
/// A motorcycle in the company fleet; shares <c>TwoWheelers_Overview</c> with <see cref="Bicycle"/>.
/// </summary>
public class Motorcycle
{
    /// <summary>Unique identifier of this motorcycle, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }
    /// <summary>Manufacturer of the motorcycle, for example <c>Honda</c>.</summary>
    public string Brand { get; set; } = string.Empty;
    /// <summary>Model name of the motorcycle.</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Engine displacement in cubic centimetres.</summary>
    public int EngineCapacity { get; set; }
}
