using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// A postal address as a consumer stores it (PRD §4.15, Q-R7: the type here, its behaviour in the client package).
/// Ids point into reference data, the names beside them are the snapshot shown when reference data is unreachable,
/// and <see cref="Lines"/> is the rendered legal snapshot an invoice keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat</b>, where PRD §4.15 sketches nested objects (<c>Street: { Id, Name }</c>): Spark's generic form edits a
/// single AsDetail value object as one record of plain fields (X15), so the address renderer reads and writes the
/// same field names the model declares. The JSON is otherwise the sketch's, one level down.
/// </para>
/// <para>
/// Free text is always possible (<see cref="Verified"/> <see langword="false"/>): new streets appear before the
/// registers have them, and many countries have no street data. House numbers are never imported (RD5).
/// </para>
/// </remarks>
[ValueObject]
public partial class Address
{
    /// <summary>ISO 3166-1 alpha-2 code of the country, upper case: <c>BE</c>.</summary>
    public string? CountryCode { get; set; }

    /// <summary>The street in reference data (<c>streets/be/…</c>), or empty for a free-text street.</summary>
    public string? StreetId { get; set; }

    /// <summary>Street name as written: <c>Kerkstraat</c>.</summary>
    public string? StreetName { get; set; }

    /// <summary>House number with its optional letter: <c>12</c>, <c>12A</c>.</summary>
    public string? HouseNumber { get; set; }

    /// <summary>Box (bus/boîte), where the country has one.</summary>
    public string? Box { get; set; }

    /// <summary>The postal code in reference data (<c>postalcodes/be/9770</c>).</summary>
    public string? PostalCodeId { get; set; }

    /// <summary>Postal code as written: <c>9770</c>.</summary>
    public string? PostalCode { get; set; }

    /// <summary>The postal locality's key within its code: <c>kruishoutem</c>.</summary>
    public string? LocalityKey { get; set; }

    /// <summary>Locality as written on the city line: <c>Kruishoutem</c>.</summary>
    public string? LocalityName { get; set; }

    /// <summary>The section or locality place, where one exists.</summary>
    public string? LocalityPlaceId { get; set; }

    /// <summary>The municipality (<c>places/be/nis/45068</c>).</summary>
    public string? MunicipalityId { get; set; }

    /// <summary>Municipality name: <c>Kruisem</c>.</summary>
    public string? MunicipalityName { get; set; }

    /// <summary>IANA time zone of the municipality: <c>Europe/Brussels</c> (D33: a venue's zone comes from its address).</summary>
    public string? TimeZone { get; set; }

    /// <summary>The rendered address lines (the legal snapshot an invoice keeps).</summary>
    public List<string> Lines { get; set; } = [];

    /// <summary>The reference-data release the ids were checked against.</summary>
    public string? Release { get; set; }

    /// <summary>The street, postal code and locality were picked from reference data.</summary>
    public bool Verified { get; set; }

    /// <summary>The house number was confirmed by a validator (RD5); false when unchecked or when the validator was unreachable.</summary>
    public bool HouseNumberVerified { get; set; }
}
