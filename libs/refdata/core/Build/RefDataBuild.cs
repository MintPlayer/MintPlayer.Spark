using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources;

namespace MintPlayer.Spark.ReferenceData.Build;

/// <summary>
/// A dataset being built from source inputs (PRD §4.5 steps 6–9): mappers add documents and lineage events, minting
/// every id through <see cref="Ledger"/>; <see cref="RefDerivation"/> then computes the derived fields. The same build
/// serves a full import, the basic seed set (RD9) and the tests, so their ids come from the same ledgers.
/// </summary>
public sealed class RefDataBuild(RefLedger ledger)
{
    public RefLedger Ledger { get; } = ledger;

    public Dictionary<string, RefCountry> Countries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefPlace> Places { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefPostalCode> PostalCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefStreet> Streets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefLineageEvent> Lineage { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefTimeZone> TimeZones { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PublicHoliday> Holidays { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VatRateSuggestion> VatRates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sources that contributed, for the manifest and <c>/api/v1/sources</c>.</summary>
    public Dictionary<string, ReleaseSource> Sources { get; } = new(StringComparer.Ordinal);

    /// <summary>Problems a mapper chose not to fail on (an unmatched row, an ambiguous name): listed on the import run.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>The valid IANA zone ids (tzdata <c>zone.tab</c>), when a source supplied them; assignments are checked against it.</summary>
    public HashSet<string> KnownTimeZones { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The countries a registered country adapter owns (<see cref="IReferenceDataMapper.IsCountryAdapter"/>), whether or
    /// not its input is part of this build: the global baseline writes no profile or units for them.
    /// </summary>
    public HashSet<string> AdapterCountries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RefPlace Place(string id) => GetOrAdd(Places, id, () => new RefPlace { Id = id, Country = RefIds.CountryOf(id) ?? string.Empty });

    public RefPostalCode PostalCode(string id, string country, string code)
        => GetOrAdd(PostalCodes, id, () => new RefPostalCode { Id = id, Country = country, Code = code });

    public RefStreet Street(string id, string country) => GetOrAdd(Streets, id, () => new RefStreet { Id = id, Country = country });

    public RefCountry Country(string code)
    {
        var lower = RefIds.Country(code);
        return GetOrAdd(Countries, RefIds.CountryId(lower), () => new RefCountry { Id = RefIds.CountryId(lower), Code = lower.ToUpperInvariant() });
    }

    public void AddLineage(RefLineageEvent lineageEvent) => Lineage[lineageEvent.Id!] = lineageEvent;

    /// <summary>Records <paramref name="input"/> as a contributing source with its licence.</summary>
    public void AddSource(SourceInput input, SourceLicence licence)
        => Sources[input.SourceId] = new ReleaseSource
        {
            Id = input.SourceId,
            Version = input.Version,
            Url = input.Url,
            Sha256 = input.Sha256,
            Licence = licence.Licence,
            LicenceUrl = licence.LicenceUrl,
            Attribution = input.Attribution ?? licence.Attribution,
        };

    /// <summary>Every document of the build (for the release writer and the importer).</summary>
    public IEnumerable<object> Documents()
        => Countries.Values.Cast<object>()
            .Concat(Places.Values).Concat(PostalCodes.Values).Concat(Streets.Values)
            .Concat(Lineage.Values).Concat(TimeZones.Values).Concat(Holidays.Values).Concat(VatRates.Values);

    private static T GetOrAdd<T>(Dictionary<string, T> map, string id, Func<T> create)
    {
        if (!map.TryGetValue(id, out var value))
            map[id] = value = create();
        return value;
    }
}
