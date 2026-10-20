using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// A country and its profile (PRD §4.2, RD-DEF 6). Country <em>names</em> are not stored: browsers use
/// <c>Intl.DisplayNames</c> and servers use CLDR (K11, RD-DEF 10, Q-R18).
/// </summary>
public sealed class RefCountry
{
    /// <summary><c>countries/{cc}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>ISO 3166-1 alpha-2, upper case: <c>BE</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Alpha3 { get; set; } = string.Empty;

    public string Numeric { get; set; } = string.Empty;

    /// <summary>The place id of the country itself (the root of every ancestor chain), when it is imported as a place.</summary>
    public string? Place { get; set; }

    public CountryProfile Profile { get; set; } = new();

    public ReleaseStamp? Release { get; set; }

    public List<string> Source { get; set; } = [];

    public string? Hash { get; set; }
}

/// <summary>What the typeahead, the address form and the formatter need to know about one country (RD-DEF 6).</summary>
public sealed class CountryProfile
{
    /// <summary>The administrative levels, top down.</summary>
    public List<AdminLevel> Levels { get; set; } = [];

    /// <summary>The <see cref="AdminLevel.Key"/> of the municipality level (the owner's <c>isCity</c>).</summary>
    public string MunicipalityLevel { get; set; } = string.Empty;

    public List<DatedLanguage> OfficialLanguages { get; set; } = [];

    /// <summary>
    /// Hand-authored address lines (libaddressinput states no licence, so nothing is copied from it). Placeholders:
    /// <c>{street}</c>, <c>{houseNumber}</c>, <c>{box}</c>, <c>{postalCode}</c>, <c>{locality}</c>, <c>{municipality}</c>.
    /// A line whose placeholders are all empty is dropped; <c>[ bus {box}]</c> is an optional part, dropped with its value.
    /// </summary>
    public List<string> AddressLines { get; set; } = [];

    /// <summary>The postal-code pattern (.NET and ECMAScript compatible), anchored.</summary>
    public string? PostcodePattern { get; set; }

    public HouseNumberRules? HouseNumbers { get; set; }

    /// <summary>The order the address form narrows in: <c>locality</c>, <c>street</c>, <c>houseNumber</c>.</summary>
    public List<string> TypeaheadSteps { get; set; } = [];

    /// <summary>The first date the imported history covers (RD4); an <c>asOf</c> before it is reported, never guessed.</summary>
    public DateOnly? HistoryKnownSince { get; set; }

    /// <summary>The zone of a single-zone country; <see langword="null"/> where places carry their own (Q-R10).</summary>
    public string? TimeZone { get; set; }

    /// <summary>Territories with a special VAT status (commerce GA4), hand-written with their legal basis.</summary>
    public List<VatTerritory> VatTerritories { get; set; } = [];
}

public sealed class AdminLevel
{
    /// <summary><c>{cc}:{name}</c>: <c>be:province</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public PlaceRole Role { get; set; }

    public int Order { get; set; }

    /// <summary>The level can be absent in a chain ("the Brussels-Capital Region has no province").</summary>
    public bool Optional { get; set; }

    public TranslatedString Labels { get; set; } = new();

    /// <summary>The kind weight of <see cref="RefPlace.Rank"/>: the city-line unit ranks highest.</summary>
    public int RankWeight { get; set; }
}

/// <summary>House numbers are free text checked by these rules (RD5).</summary>
public sealed class HouseNumberRules
{
    /// <summary>The number with its optional letter: <c>^[1-9][0-9]{0,4}[A-Za-z]?$</c>.</summary>
    public string NumberPattern { get; set; } = string.Empty;

    /// <summary>The box, or <see langword="null"/> where the country has none.</summary>
    public string? BoxPattern { get; set; }
}

/// <summary>A territory of a member state with a special VAT status (commerce GA4, PRD §4.4 "Optional sources").</summary>
public sealed class VatTerritory
{
    /// <summary>A stable key: ISO 3166-2 where one exists (<c>FR-GP</c>), else a slug (<c>DE-HELGOLAND</c>).</summary>
    public string Code { get; set; } = string.Empty;

    public TranslatedString Names { get; set; } = new();

    public VatTerritoryTreatment Treatment { get; set; }

    /// <summary>Postcodes inside the territory, anchored (<c>^971\d{2}$</c>).</summary>
    public string? PostcodePattern { get; set; }

    /// <summary>The article that gives the status, e.g. <c>Directive 2006/112/EC, Art. 6(2)(a)</c>.</summary>
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>What could not be confirmed, or what a host must decide.</summary>
    public string? Note { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<VatTerritoryTreatment>))]
public enum VatTerritoryTreatment
{
    /// <summary>In the EU customs territory, outside the VAT territory (Directive 2006/112/EC Art. 6(1)).</summary>
    OutsideVatInsideCustoms,
    /// <summary>Outside both (Art. 6(2)).</summary>
    OutsideVatAndCustoms,
    /// <summary>Not part of the EU at all (an overseas country or territory).</summary>
    OutsideEu,
}
