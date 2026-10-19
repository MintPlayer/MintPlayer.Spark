using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// One postal code (PRD §4.2). Its <see cref="Localities"/> are the (code, name) pairs people write on the city line;
/// postal codes are many-to-many with places (9750 = Huise, Ouwegem, Zingem). History comes from lineage: a
/// locality links to its main municipality over time.
/// </summary>
public sealed class RefPostalCode
{
    /// <summary><c>postalcodes/{cc}/{code}</c>.</summary>
    public string? Id { get; set; }

    public string Country { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public List<PostalLocality> Localities { get; set; } = [];

    public ReleaseStamp? Release { get; set; }

    public List<string> Source { get; set; } = [];

    public string? Hash { get; set; }
}

public sealed class PostalLocality
{
    /// <summary>The folded compact name, unique within the code: <c>kruishoutem</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public TranslatedString Names { get; set; } = new();

    /// <summary>The section or locality place of the same name, where one exists.</summary>
    public string? Place { get; set; }

    /// <summary>The municipality the locality lies in, over time.</summary>
    public List<DatedLink> Municipalities { get; set; } = [];

    /// <summary>The source marks this locality as the code's main one (BeST: written in capitals).</summary>
    public bool Main { get; set; }

    public SearchKeys Search { get; set; } = new();

    /// <summary>Rank of the entry in the typeahead.</summary>
    public long Rank { get; set; }
}

/// <summary>A dated link to another document.</summary>
public sealed class DatedLink
{
    public string Id { get; set; } = string.Empty;
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    /// <summary>Derived <see cref="DateKeys"/> of <see cref="From"/> and <see cref="To"/>, for index range predicates.</summary>
    public int FromKey { get; set; }
    public int ToKey { get; set; }
}

/// <summary>
/// One street (PRD §4.2): dated municipality and postal-code links, dated names, a type and the homonym qualifier.
/// Periods use the <em>legal</em> date, never the register's version timestamp.
/// </summary>
public sealed class RefStreet
{
    /// <summary><c>streets/{cc}/{region}/{objectId}</c>: the authority's stable identifier (BeST namespace + id).</summary>
    public string? Id { get; set; }

    public string Country { get; set; } = string.Empty;

    public TranslatedString Names { get; set; } = new();

    public StreetType Type { get; set; }

    /// <summary>The homonym addition BeST publishes ("Kerkstraat (Ouwegem)").</summary>
    public string? Qualifier { get; set; }

    public List<StreetPeriod> Periods { get; set; } = [];

    public List<PlaceCode> Codes { get; set; } = [];

    public SearchKeys Search { get; set; } = new();

    public ReleaseStamp? Release { get; set; }

    public List<string> Source { get; set; } = [];

    public string? Hash { get; set; }
}

public sealed class StreetPeriod
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string Municipality { get; set; } = string.Empty;
    public List<string> PostalCodes { get; set; } = [];

    /// <summary>The Walloon part of municipality (Q-R16) or section the street lies in, where the source states it.</summary>
    public string? Locality { get; set; }

    public int FromKey { get; set; }
    public int ToKey { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<StreetType>))]
public enum StreetType
{
    Streetname,
    Hamlet,
}
