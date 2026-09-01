using DemoApp.Library.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Indexes;

/// <summary>
/// The row shape of the <c>company-cars</c> sub-query.
/// </summary>
/// <remarks>
/// Carries every attribute <c>Car.json</c> marks <c>ShowedOn.Query</c> — that set is where the
/// grid's columns come from, and a property missing here renders as an empty cell rather than an
/// error. <see cref="CompanyId"/> is the extra: it is what the query filters on, and it is not on
/// <c>VCar</c>, which is why this projection exists at all.
/// </remarks>
[FromIndex(typeof(Company_Cars))]
public partial class VCompanyCar
{
    public string? Id { get; set; }

    /// <summary>The owning company's document id — the sub-query's filter, never a column.</summary>
    public string? CompanyId { get; set; }

    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? OwnerFullName { get; set; }

    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }
}
