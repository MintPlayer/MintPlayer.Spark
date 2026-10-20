using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// One unit of territorial organisation as its authority recognises it (PRD §4.2, RD3). Its identity survives
/// name, code, parent and boundary changes; it ends only when the unit ends. When a unit carries on one level down
/// (municipality → section), the same document gets a new dated <see cref="Periods">period</see> with the new kind
/// and parent: the owner's "isCity true → false plus a parent", stored as history instead of an overwrite.
/// </summary>
/// <remarks>
/// <see cref="Periods"/>, <see cref="Names"/>, <see cref="Codes"/>, <see cref="OfficialLanguages"/>,
/// <see cref="TimeZone"/> and <see cref="Population"/> are imported. Everything else is <b>derived</b> by the build
/// (<c>RefDerivation</c> in the instance package) and never authored. No time-dependent field is stored: "current"
/// is computed from an as-of date (<see cref="RefAsOf"/>), so a merger published in advance becomes effective at
/// midnight without a rebuild.
/// </remarks>
public sealed class RefPlace
{
    /// <summary><c>places/{cc}/{scheme}/{code}[@yyyy-MM-dd]</c> (§4.3): minted once, never re-derived.</summary>
    public string? Id { get; set; }

    /// <summary>ISO 3166-1 alpha-2, lower case: <c>be</c>.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Half-open, non-overlapping, in date order; a gap means the unit did not exist.</summary>
    public List<PlacePeriod> Periods { get; set; } = [];

    /// <summary>Dated names (X17): official names per language, exonyms, historic names (still searchable).</summary>
    public TranslatedString Names { get; set; } = new();

    public List<PlaceCode> Codes { get; set; } = [];

    public List<DatedLanguage> OfficialLanguages { get; set; } = [];

    /// <summary>The IANA zone id, or <see langword="null"/> with <see cref="TimeZoneNote"/> saying why (Q-R10).</summary>
    public string? TimeZone { get; set; }

    public string? TimeZoneNote { get; set; }

    /// <summary>Where a source states it; feeds <see cref="Rank"/>.</summary>
    public long? Population { get; set; }

    // ---- derived ----

    public DateRange Lifetime { get; set; } = new();

    /// <summary>The periods cut at every ancestor change, each with its full ancestor chain and integer date keys.</summary>
    public List<PlaceSegment> Segments { get; set; } = [];

    public List<SuccessionEdge> Supersedes { get; set; } = [];

    public List<SuccessionEdge> SupersededBy { get; set; } = [];

    /// <summary>A lineage event that could not be resolved without curation ended this place (Q-R4).</summary>
    public bool LineageUnresolved { get; set; }

    public SearchKeys Search { get; set; } = new();

    /// <summary>The kind weight from the country profile, then population; higher ranks first (§4.10).</summary>
    public long Rank { get; set; }

    public ReleaseStamp? Release { get; set; }

    /// <summary>The source ids that contributed (attribution per record, §4.12).</summary>
    public List<string> Source { get; set; } = [];

    /// <summary><c>sha256:…</c> over the canonical document without <see cref="Hash"/> and <see cref="Release"/>.</summary>
    public string? Hash { get; set; }
}

/// <summary>One stretch of a place's life with one kind and one parent: <c>[From, To)</c>, <see langword="null"/> = open.</summary>
public sealed class PlacePeriod
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    /// <summary>A level key from the country profile: <c>be:municipality</c>, <c>be:section</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public PlaceRole Role { get; set; }

    /// <summary>The parent place id, or <see langword="null"/> for a country.</summary>
    public string? Parent { get; set; }
}

/// <summary>A code a place held, with its validity: <c>nis6 45017A</c> until 2025, <c>45017Z</c> after.</summary>
public sealed class PlaceCode
{
    /// <summary>The code space: <c>nis</c>, <c>nis6</c>, <c>insee</c>, <c>geonames</c>, <c>iso3166-2</c>, <c>wikidata</c>.</summary>
    public string Scheme { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    /// <summary>Derived <see cref="DateKeys"/> of <see cref="From"/>/<see cref="To"/> (index range predicates).</summary>
    public int FromKey { get; set; }
    public int ToKey { get; set; }
}

public sealed class DatedLanguage
{
    /// <summary>RFC 5646 tag: <c>nl</c>, <c>fr</c>.</summary>
    public string Lang { get; set; } = string.Empty;
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed class DateRange
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    public bool Contains(DateOnly day) => (From is null || From.Value <= day) && (To is null || day < To.Value);
}

/// <summary>
/// A period cut at every ancestor change. <see cref="FromKey"/>/<see cref="ToKey"/> are <c>yyyyMMdd</c> integers with
/// <c>0</c> for an open start and <c>99991231</c> for an open end, because RavenDB range predicates never match null
/// or absent values (<see cref="DateKeys"/>).
/// </summary>
public sealed class PlaceSegment
{
    public int FromKey { get; set; }
    public int ToKey { get; set; }
    public string Kind { get; set; } = string.Empty;
    public PlaceRole Role { get; set; }
    public string? Parent { get; set; }

    /// <summary>Parent first, the country's top level last.</summary>
    public List<string> Ancestors { get; set; } = [];
}

/// <summary>A direct lineage edge: the other place, the legal date, the event, and whether all or part of the unit went there.</summary>
public sealed class SuccessionEdge
{
    public string Id { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string Event { get; set; } = string.Empty;
    public SuccessionShare Share { get; set; }
}

/// <summary>Import-time search keys (§4.10): computed once by one versioned pipeline, shipped as data.</summary>
public sealed class SearchKeys
{
    public int KeyVersion { get; set; }

    /// <summary>Folded single-word tokens of every name (historic included), with article- and abbreviation-class variants.</summary>
    public List<string> Tokens { get; set; } = [];

    /// <summary>Whole names folded with spaces, hyphens and apostrophes removed: <c>sintmartenslatem</c>.</summary>
    public List<string> Compact { get; set; } = [];
}

/// <summary>The release in which a document first appeared and in which its <c>Hash</c> last changed (§4.6).</summary>
public sealed class ReleaseStamp
{
    public string Introduced { get; set; } = string.Empty;
    public string Changed { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaceRole>))]
public enum PlaceRole
{
    Country,
    Region,
    Subregion,
    Municipality,
    Submunicipality,
    Locality,
}

[JsonConverter(typeof(JsonStringEnumConverter<SuccessionShare>))]
public enum SuccessionShare
{
    Whole,
    Part,
}
