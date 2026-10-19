using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Indexes;

/// <summary>
/// The plugin's index declarations (PRD §4.10, Q-R6 = RX4/X19): the application's generator emits the index classes,
/// so indexes live in the application (owner rule) and every host gets the same ones. Queried by name.
/// <list type="table">
/// <item><term><c>RefLocalities/Typeahead</c></term><description>Places and postal localities, one entry per validity segment.
/// <see cref="TypeaheadEntry.Tokens"/> is <c>[Fuzzy]</c>, so the index runs Lucene (fuzzy by default, RD10).</description></item>
/// <item><term><c>RefStreets/Typeahead</c></term><description>One entry per street period; always filtered by municipality or postal code.</description></item>
/// <item><term><c>RefPlaces/ByParent</c></term><description>Children as of a date.</description></item>
/// <item><term><c>RefPlaces/ByCode</c></term><description>Who held code X on date D.</description></item>
/// <item><term><c>RefLineageEvents/ByParticipant</c></term><description>Lineage per place.</description></item>
/// </list>
/// Every field uses the default keyword analyzer: no <c>search()</c>, whose stop words make the municipality "As" unfindable.
/// </summary>
public static class RefDataIndexes
{
    public const string Localities = "RefLocalities/Typeahead";
    public const string Streets = "RefStreets/Typeahead";
    public const string ByParent = "RefPlaces/ByParent";
    public const string ByCode = "RefPlaces/ByCode";
    public const string ByParticipant = "RefLineageEvents/ByParticipant";

    [SparkIndexMap("RefLocalities_Typeahead", StoreAllFields = true)]
    public static Expression<Func<IEnumerable<RefPlace>, IEnumerable<TypeaheadEntry>>> PlaceEntries { get; } =
        places => from place in places
                  from segment in place.Segments
                  select new TypeaheadEntry
                  {
                      Type = "place",
                      DocumentId = place.Id!,
                      Country = place.Country,
                      Kind = segment.Kind,
                      FromKey = segment.FromKey,
                      ToKey = segment.ToKey,
                      Tokens = place.Search.Tokens,
                      Compact = place.Search.Compact,
                      Rank = place.Rank,
                      Within = segment.Ancestors,
                      PostalCode = null,
                      LocalityKey = null,
                      Place = place.Id,
                      Municipality = null,
                  };

    [SparkIndexMap("RefLocalities_Typeahead", StoreAllFields = true)]
    public static Expression<Func<IEnumerable<RefPostalCode>, IEnumerable<TypeaheadEntry>>> PostalEntries { get; } =
        codes => from code in codes
                 from locality in code.Localities
                 from municipality in locality.Municipalities
                 select new TypeaheadEntry
                 {
                     Type = "postal-locality",
                     DocumentId = code.Id!,
                     Country = code.Country,
                     Kind = "postal-locality",
                     FromKey = municipality.FromKey,
                     ToKey = municipality.ToKey,
                     Tokens = locality.Search.Tokens,
                     Compact = locality.Search.Compact,
                     Rank = locality.Rank,
                     Within = new[] { municipality.Id },
                     PostalCode = code.Code,
                     LocalityKey = locality.Key,
                     Place = locality.Place,
                     Municipality = municipality.Id,
                 };

    [SparkIndexMap("RefStreets_Typeahead", StoreAllFields = true)]
    public static Expression<Func<IEnumerable<RefStreet>, IEnumerable<StreetTypeaheadEntry>>> StreetEntries { get; } =
        streets => from street in streets
                   from period in street.Periods
                   select new StreetTypeaheadEntry
                   {
                       DocumentId = street.Id!,
                       Country = street.Country,
                       Municipality = period.Municipality,
                       PostalCodes = period.PostalCodes,
                       FromKey = period.FromKey,
                       ToKey = period.ToKey,
                       Tokens = street.Search.Tokens,
                       Compact = street.Search.Compact,
                   };

    [SparkIndexMap("RefPlaces_ByParent")]
    public static Expression<Func<IEnumerable<RefPlace>, IEnumerable<PlaceParentEntry>>> Children { get; } =
        places => from place in places
                  from segment in place.Segments
                  select new PlaceParentEntry { Parent = segment.Parent, Kind = segment.Kind, FromKey = segment.FromKey, ToKey = segment.ToKey };

    [SparkIndexMap("RefPlaces_ByCode")]
    public static Expression<Func<IEnumerable<RefPlace>, IEnumerable<PlaceCodeEntry>>> Codes { get; } =
        places => from place in places
                  from code in place.Codes
                  select new PlaceCodeEntry
                  {
                      Country = place.Country,
                      Scheme = code.Scheme,
                      Value = code.Value,
                      FromKey = code.FromKey,
                      ToKey = code.ToKey,
                  };

    [SparkIndexMap("RefLineageEvents_ByParticipant")]
    public static Expression<Func<IEnumerable<RefLineageEvent>, IEnumerable<LineageParticipantEntry>>> Participants { get; } =
        events => from lineage in events
                  from op in lineage.Operations
                  select new LineageParticipantEntry { Place = op.Place, Date = lineage.Date };
}

/// <summary>An entry of <c>RefLocalities/Typeahead</c>: one place segment or one (postal locality, municipality period).</summary>
public sealed class TypeaheadEntry
{
    /// <summary><c>place</c> or <c>postal-locality</c>.</summary>
    public string Type { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int FromKey { get; set; }
    public int ToKey { get; set; }

    /// <summary>Whole-token fuzzy matching (step 2) runs on these: the opt-in that puts the index on Lucene.</summary>
    [Fuzzy]
    public List<string> Tokens { get; set; } = [];
    public List<string> Compact { get; set; } = [];
    public long Rank { get; set; }

    /// <summary>The ancestors of the segment (<c>within</c> filters on it).</summary>
    public IEnumerable<string> Within { get; set; } = [];
    public string? PostalCode { get; set; }
    public string? LocalityKey { get; set; }
    public string? Place { get; set; }
    public string? Municipality { get; set; }
}

public sealed class StreetTypeaheadEntry
{
    public string DocumentId { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Municipality { get; set; } = string.Empty;
    public List<string> PostalCodes { get; set; } = [];
    public int FromKey { get; set; }
    public int ToKey { get; set; }

    [Fuzzy]
    public List<string> Tokens { get; set; } = [];
    public List<string> Compact { get; set; } = [];
}

public sealed class PlaceParentEntry
{
    public string? Parent { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int FromKey { get; set; }
    public int ToKey { get; set; }
}

public sealed class PlaceCodeEntry
{
    public string Country { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int FromKey { get; set; }
    public int ToKey { get; set; }
}

public sealed class LineageParticipantEntry
{
    public string Place { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
}
