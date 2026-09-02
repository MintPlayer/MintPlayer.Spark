namespace HR.Entities;

public class AddressDescription
{
    /// <summary>Short label for the address, e.g. <c>Home</c> or <c>Head office</c>.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Free-text remarks about the address, such as access or delivery instructions.</summary>
    public string Notes { get; set; } = string.Empty;
}
