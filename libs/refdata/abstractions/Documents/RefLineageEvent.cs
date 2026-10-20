using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// One legal change for one receiving unit (PRD §4.2, RD3): the 1977 fusion is about 600 events, not one document.
/// <see cref="RefPlace.Supersedes"/> and <see cref="RefPlace.SupersededBy"/> are derived from the operations.
/// </summary>
public sealed class RefLineageEvent
{
    /// <summary><c>lineage/{cc}/{yyyy-MM-dd}/{kind}/{scheme}-{code}</c>.</summary>
    public string? Id { get; set; }

    public string Country { get; set; } = string.Empty;

    /// <summary>The legal date, from the country's primary source (Q-R3).</summary>
    public DateOnly Date { get; set; }

    public LineageKind Kind { get; set; }

    public List<LineageOperation> Operations { get; set; } = [];

    /// <summary>What each source said, including dates that disagree with <see cref="Date"/> (Bastogne, Q-R3).</summary>
    public List<LineageSource> Sources { get; set; } = [];

    /// <summary>The successors could not be derived without curation (Q-R4); the places end, flagged.</summary>
    public bool Unresolved { get; set; }

    public ReleaseStamp? Release { get; set; }

    public string? Hash { get; set; }
}

public sealed class LineageOperation
{
    public string Place { get; set; } = string.Empty;
    public LineageOp Op { get; set; }

    /// <summary>For <see cref="LineageOp.End"/>, <see cref="LineageOp.Cede"/> and <see cref="LineageOp.Demote"/>: where the unit (or a part) went.</summary>
    public string? Into { get; set; }

    public SuccessionShare Share { get; set; } = SuccessionShare.Whole;
}

public sealed class LineageSource
{
    public string Source { get; set; } = string.Empty;
    public DateOnly? Date { get; set; }
    public string? Note { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<LineageKind>))]
public enum LineageKind
{
    Merge,
    Absorb,
    Demote,
    Split,
    Cede,
    Rename,
    Recode,
    Reparent,
    Restore,
}

[JsonConverter(typeof(JsonStringEnumConverter<LineageOp>))]
public enum LineageOp
{
    Continue,
    End,
    Demote,
    Promote,
    Cede,
    Receive,
    Rename,
    Recode,
    Reparent,
    Create,
}

/// <summary>A time zone's translated city names (CLDR exemplar cities, §4.13). Only the names CLDR states; an absent language inherits the city from the id.</summary>
public sealed class RefTimeZone
{
    /// <summary><c>timezones/{IANA id}</c>: <c>timezones/Europe/Brussels</c>.</summary>
    public string? Id { get; set; }

    public string Zone { get; set; } = string.Empty;

    public TranslatedString Names { get; set; } = new();

    public List<string> Countries { get; set; } = [];

    public ReleaseStamp? Release { get; set; }

    public List<string> Source { get; set; } = [];

    public string? Hash { get; set; }
}

/// <summary>A public holiday (commerce GC11), per country or per region where they differ.</summary>
public sealed class PublicHoliday
{
    /// <summary><c>holidays/{cc}/{yyyy-MM-dd}</c>, or <c>holidays/{cc}-{subdivision}/{yyyy-MM-dd}</c>.</summary>
    public string? Id { get; set; }

    public string Country { get; set; } = string.Empty;

    /// <summary>ISO 3166-2 subdivision code where the holiday is regional, else <see langword="null"/>.</summary>
    public string? Subdivision { get; set; }

    public DateOnly Date { get; set; }

    public TranslatedString Names { get; set; } = new();

    /// <summary>The statute that makes the day a holiday.</summary>
    public string LegalBasis { get; set; } = string.Empty;

    public ReleaseStamp? Release { get; set; }

    public List<string> Source { get; set; } = [];

    public string? Hash { get; set; }
}

/// <summary>
/// A VAT rate as the Commission's TEDB states it (commerce GC10). A <b>suggestion</b>: commerce shows it, the host
/// sets its own rates (commerce §4.8). Never applied automatically.
/// </summary>
public sealed class VatRateSuggestion
{
    /// <summary><c>vatrates/{cc}/{type}/{category}/{situationOn}</c>.</summary>
    public string? Id { get; set; }

    public string Country { get; set; } = string.Empty;

    /// <summary>TEDB's rate type: <c>STANDARD</c>, <c>REDUCED</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>TEDB's rate value type: <c>DEFAULT</c>, <c>REDUCED_RATE</c>, <c>SUPER_REDUCED_RATE</c>, <c>EXEMPTED</c>.</summary>
    public string RateType { get; set; } = string.Empty;

    /// <summary>The rate in basis points (21 % = 2100): integers only in canonical files.</summary>
    public int RateBasisPoints { get; set; }

    /// <summary>The TEDB category identifier, or <c>standard</c>.</summary>
    public string Category { get; set; } = string.Empty;

    public string? CategoryDescription { get; set; }

    /// <summary>The date the rate was retrieved for (TEDB <c>situationOn</c>).</summary>
    public DateOnly SituationOn { get; set; }

    public string? Comment { get; set; }

    public ReleaseStamp? Release { get; set; }

    public List<string> Source { get; set; } = [];

    public string? Hash { get; set; }
}

/// <summary>A recorded dataset release on the instance (<c>refreleases/{release}</c>, §4.6).</summary>
public sealed class RefRelease
{
    public string? Id { get; set; }
    public string Release { get; set; } = string.Empty;
    public string? Previous { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>The SHA-256 of the manifest bytes.</summary>
    public string ManifestSha256 { get; set; } = string.Empty;

    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The one <c>refdata/state</c> document per database: the release it holds, written last (§4.7).</summary>
public sealed class RefDataState
{
    public const string DocumentId = "refdata/state";

    public string? Id { get; set; } = DocumentId;

    public string? Release { get; set; }

    /// <summary>Per collection: the digest over (id, Hash) of what the database holds.</summary>
    public Dictionary<string, string> Digests { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset? AppliedAtUtc { get; set; }
}
