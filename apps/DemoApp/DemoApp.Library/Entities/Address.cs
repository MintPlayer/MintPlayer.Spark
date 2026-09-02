namespace DemoApp.Library.Entities;

public class Address
{
    /// <summary>Unique identifier of this address, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }
    /// <summary>Street name and house number, for example <c>Main Street 12</c>.</summary>
    public string Street { get; set; } = string.Empty;
    /// <summary>City or town the address is located in.</summary>
    public string City { get; set; } = string.Empty;
    /// <summary>State, province or region the city belongs to.</summary>
    public string State { get; set; } = string.Empty;
}
