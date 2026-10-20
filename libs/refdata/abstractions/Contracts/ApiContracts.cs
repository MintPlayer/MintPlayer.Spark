using MintPlayer.Spark.Abstractions;

[assembly: SparkClientModule("MintPlayer.Spark.ReferenceData.Contracts", "@mintplayer/ng-spark/reference-data")]

namespace MintPlayer.Spark.ReferenceData.Contracts;

// The public API's responses (PRD §4.11, /api/v1). One versioned contract for the instance, the .NET client and the
// TypeScript entry point (generated into libs/node_packages/ng-spark/reference-data/src/generated/contracts.ts).
// Every response carries the release it was answered from and the attribution of the data in it (§4.12).

/// <summary><c>GET /api/v1/suggest</c>: places and postal localities.</summary>
[SparkContract]
public sealed record SuggestResponse(
    string Release,
    string AsOf,
    IReadOnlyList<string> Attribution,
    IReadOnlyList<SuggestItem> Items,
    string? HistoryKnownSince);

/// <summary>One suggestion. <c>Type</c> is <c>place</c> or <c>postal-locality</c>.</summary>
[SparkContract]
public sealed record SuggestItem(
    string Type,
    string Label,
    string? Secondary,
    SuggestMatch Matched,
    SuggestPostalCode? PostalCode,
    SuggestLocality? Locality,
    SuggestRef? Place,
    SuggestRef? Municipality,
    IReadOnlyList<SuggestPathItem> Path,
    string? TimeZone,
    string Status,
    IReadOnlyList<SuggestRef> Successors,
    string Country);

/// <summary>The name that matched, with the matched character ranges (start, end exclusive).</summary>
[SparkContract]
public sealed record SuggestMatch(string Value, string Lang, string? Kind, IReadOnlyList<IReadOnlyList<int>> Ranges, bool Fuzzy);

[SparkContract]
public sealed record SuggestPostalCode(string Id, string Code);

[SparkContract]
public sealed record SuggestLocality(string Key, string Name, string? Place);

[SparkContract]
public sealed record SuggestRef(string Id, string Name);

[SparkContract]
public sealed record SuggestPathItem(string Id, string Kind, string Name);

/// <summary><c>GET /api/v1/streets/suggest</c>.</summary>
[SparkContract]
public sealed record StreetSuggestResponse(
    string Release,
    string AsOf,
    IReadOnlyList<string> Attribution,
    IReadOnlyList<StreetSuggestItem> Items);

[SparkContract]
public sealed record StreetSuggestItem(
    string Id,
    string Label,
    string? Qualifier,
    string Type,
    SuggestMatch Matched,
    SuggestRef Municipality,
    IReadOnlyList<string> PostalCodes,
    bool Fuzzy);

/// <summary><c>GET /api/v1/places/{id}</c> and the items of <c>/resolve</c>.</summary>
[SparkContract]
public sealed record PlaceResponse(
    string Release,
    string AsOf,
    IReadOnlyList<string> Attribution,
    PlaceItem? Place,
    IReadOnlyList<PlaceItem> ResolvedTo,
    bool Ambiguous,
    bool BeforeHistory);

[SparkContract]
public sealed record PlaceItem(
    string Id,
    string Country,
    string Name,
    string Local,
    string? Ui,
    string? Latin,
    string? Kind,
    string? Role,
    IReadOnlyList<SuggestPathItem> Path,
    IReadOnlyList<PlaceCodeItem> Codes,
    string? TimeZone,
    string Status,
    IReadOnlyList<SuccessionItem> Successors,
    IReadOnlyList<SuccessionItem> Predecessors,
    string? From,
    string? To);

[SparkContract]
public sealed record PlaceCodeItem(string Scheme, string Value, string? From, string? To);

[SparkContract]
public sealed record SuccessionItem(string Id, string Name, string Date, string Event, string Share);

/// <summary><c>GET /api/v1/resolve?ids=</c>.</summary>
[SparkContract]
public sealed record ResolveResponse(string Release, string AsOf, IReadOnlyList<string> Attribution, IReadOnlyList<PlaceResponse> Items);

/// <summary><c>GET /api/v1/postalcodes/{cc}/{code}</c>.</summary>
[SparkContract]
public sealed record PostalCodeResponse(string Release, IReadOnlyList<string> Attribution, string? Id, string Code, IReadOnlyList<PostalLocalityItem> Localities);

[SparkContract]
public sealed record PostalLocalityItem(string Key, string Name, string? Place, SuggestRef? Municipality, bool Main);

/// <summary><c>GET /api/v1/countries</c>: the countries this instance holds.</summary>
[SparkContract]
public sealed record CountriesResponse(string Release, IReadOnlyList<string> Attribution, IReadOnlyList<CountryItem> Items);

/// <summary>A country; <c>Name</c> is CLDR's name in the requested language (non-browser consumers, RD-DEF 10).</summary>
[SparkContract]
public sealed record CountryItem(string Code, string Name, string? TimeZone, bool HasStreets);

/// <summary><c>GET /api/v1/countries/{cc}/profile</c>.</summary>
[SparkContract]
public sealed record CountryProfileResponse(
    string Release,
    string Code,
    IReadOnlyList<AdminLevelItem> Levels,
    string MunicipalityLevel,
    IReadOnlyList<string> OfficialLanguages,
    IReadOnlyList<string> AddressLines,
    string? PostcodePattern,
    string? HouseNumberPattern,
    string? BoxPattern,
    IReadOnlyList<string> TypeaheadSteps,
    string? HistoryKnownSince,
    string? TimeZone,
    IReadOnlyList<VatTerritoryItem> VatTerritories);

[SparkContract]
public sealed record AdminLevelItem(string Key, string Role, int Order, bool Optional, string Label);

[SparkContract]
public sealed record VatTerritoryItem(string Code, string Name, string Treatment, string? PostcodePattern, string LegalBasis, string? Note);

/// <summary><c>GET /api/v1/timezones?lang=</c>: IANA id → CLDR exemplar city, only where CLDR states one (D36 step 1).</summary>
[SparkContract]
public sealed record TimeZonesResponse(string Release, string Lang, IReadOnlyList<string> Attribution, Dictionary<string, string> Names);

/// <summary><c>GET /api/v1/sources</c>: every source with its licence and credit line (§4.12), and the API terms.</summary>
[SparkContract]
public sealed record SourcesResponse(string Release, IReadOnlyList<SourceItem> Sources, string Terms);

[SparkContract]
public sealed record SourceItem(string Id, string Country, string Name, string Licence, string? LicenceUrl, string Attribution, string? Version, bool Modified);

/// <summary><c>GET /api/v1/by-code/{cc}/{scheme}/{code}?asOf=</c>: who held the code on that date.</summary>
[SparkContract]
public sealed record ByCodeResponse(string Release, string AsOf, IReadOnlyList<string> Attribution, IReadOnlyList<PlaceItem> Items);

/// <summary>The body of a 400 or 429 (RX1).</summary>
[SparkContract]
public sealed record ApiProblem(string Error, string? Detail);
