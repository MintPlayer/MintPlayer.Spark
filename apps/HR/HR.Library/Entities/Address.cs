using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

public class Address
{
    public string Street { get; set; } = string.Empty;
    /// <summary>Postal code as the carrier prints it, e.g. <c>9000</c> for Ghent.</summary>
    public string PostalCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public AddressDescription? Description { get; set; }

    // The type's declared breadcrumb value: persisted into the document (get-only properties
    // serialize), hidden from the model, and read by the generated AddressSort companion so the
    // Address column sorts. Computed getters must stay null-safe — they run on every save.
    [Breadcrumb, IgnoreProperty]
    public string Crumb => $"{Street}, {PostalCode} {City}";
}
