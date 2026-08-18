using DemoApp.Indexes;
using DemoApp.Library.LookupReferences;
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

    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }
    /// <summary>
    /// Sort companion for <c>LicensePlate</c>. The base field is analyzed for search, which tokenizes it, so ordering
    /// on it is meaningless for a value containing spaces. This carries the same value with no indexing
    /// declared, which keeps it a single un-tokenized term.
    /// </summary>
    [IgnoreProperty]
    public string LicensePlateSort { get; set; } = string.Empty;

    /// <summary>
    /// Sort companion for <c>OwnerFullName</c>. The base field is analyzed for search, which tokenizes it, so ordering
    /// on it is meaningless for a value containing spaces. This carries the same value with no indexing
    /// declared, which keeps it a single un-tokenized term.
    /// </summary>
    [IgnoreProperty]
    public string? OwnerFullNameSort { get; set; }
}
