using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Slovenia;

/// <summary>
/// The Slovenian mapper (PRD §4.4): statistical regions, municipalities (by their code: <c>places/si/obcina/90</c>),
/// settlements and streets (by their EID, persistent since the March 2024 renewal: <c>places/si/naselje/{eid}</c>,
/// <c>streets/si/eid/{eid}</c>) and postal codes, linked through the address register. Names are Slovenian, plus the
/// Italian or Hungarian official name in the bilingual municipalities (<c>NAZIV_DJ</c>).
/// </summary>
/// <remarks>
/// <b>Postcode linkage, verified at field level</b> on the real WFS (2026-10-19): every address row carries
/// <c>EID_ULICA</c> (empty where the settlement has no street system), <c>EID_NASELJE</c> and
/// <c>POSTNI_OKOLIS_SIFRA</c>, so street ↔ postcode and settlement ↔ postcode come from it. A street may lie in another
/// postal area than its settlement (Arze in Portorož is in 6330 Piran).
/// </remarks>
internal sealed class SloveniaMapper : IReferenceDataMapper
{
    public IReadOnlyList<string> Countries => ["si"];

    public IReadOnlyList<string> SourceIds => [SloveniaUnitsSource.SourceId, SloveniaAddressesSource.SourceId];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(SloveniaMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        RefAdapterKit.Country(build, "si", "SVN", "705", SloveniaProfile.Create(), SloveniaUnitsSource.SourceId);
        var run = new Run(build, RefAdapterKit.CountryPlace(build, "si", SloveniaUnitsSource.SourceId, SloveniaProfile.Zone, "sl"));
        if (inputs.TryGetValue(SloveniaUnitsSource.SourceId, out var units))
            run.MapUnits(units);
        if (inputs.TryGetValue(SloveniaAddressesSource.SourceId, out var addresses))
            run.MapAddresses(addresses);
        return Task.CompletedTask;
    }

    private sealed class Run(RefDataBuild build, string country)
    {
        private const string Units = SloveniaUnitsSource.SourceId;
        private const string Addresses = SloveniaAddressesSource.SourceId;

        private readonly Dictionary<string, string> regionByEid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Id, string Code)> municipalityByEid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> settlementByEid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> streetByEid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Sl, string? Other)> postalNames = new(StringComparer.Ordinal);

        private IEnumerable<RefCsvRow> Rows(SourceInput input, string layer)
            => input.Files.FirstOrDefault(f => SloveniaFiles.Layer().Match(f.Name) is { Success: true } m && m.Groups["layer"].Value == layer) is { } file
                ? RefCsv.ReadRecords(file.Open(), ',')
                : [];

        public void MapUnits(SourceInput input)
        {
            foreach (var row in Rows(input, "STATISTICNE_REGIJE"))
            {
                var id = build.Ledger.Mint(Units, "si-statreg:" + row["SIFRA"], RefIds.Place("si", "statreg", row["SIFRA"]));
                Unit(id, "si:region", PlaceRole.Region, country, row, null, "si-statreg", row["SIFRA"]);
                regionByEid[row["EID_STATISTICNA_REGIJA"]] = id;
            }

            foreach (var row in Rows(input, "OBCINE"))
            {
                var code = row["SIFRA"];
                var id = build.Ledger.Mint(Units, "si-obcina:" + code, RefIds.Place("si", "obcina", code));
                // The parent (a statistical region) comes from the address register; until then the country.
                Unit(id, "si:municipality", PlaceRole.Municipality, country, row, SecondLanguage(code, row), "si-obcina", code);
                build.Place(id).Codes.Add(new PlaceCode { Scheme = "si-eid", Value = row["EID_OBCINA"] });
                municipalityByEid[row["EID_OBCINA"]] = (id, code);
            }

            foreach (var row in Rows(input, "NASELJA"))
            {
                var eid = row["EID_NASELJE"];
                if (!municipalityByEid.TryGetValue(row["EID_OBCINA"], out var municipality))
                {
                    build.Warnings.Add($"{Units}: settlement {eid} lies in municipality {row["EID_OBCINA"]}, which the input lacks: skipped.");
                    continue;
                }
                var id = build.Ledger.Mint(Units, "si-naselje:" + eid, RefIds.Place("si", "naselje", eid));
                Unit(id, "si:settlement", PlaceRole.Locality, municipality.Id, row, SecondLanguage(municipality.Code, row), "si-eid", eid);
                build.Place(id).Codes.Add(new PlaceCode { Scheme = "si-naselje", Value = row["SESTAVLJENA_SIFRA"] });
                settlementByEid[eid] = id;
            }

            foreach (var row in Rows(input, "ULICE"))
            {
                var eid = row["EID_ULICA"];
                if (!municipalityByEid.TryGetValue(row["EID_OBCINA"], out var municipality))
                    continue;
                var id = build.Ledger.Mint(Units, "si-ulica:" + eid, RefIds.Street("si", "eid", eid));
                var street = build.Street(id, "si");
                street.Names = new TranslatedString { Entries = [RefAdapterKit.Name("sl", row["NAZIV"], TranslatedStringKind.Official, Units)] };
                if (SecondLanguage(municipality.Code, row) is { } other)
                    street.Names.Entries.Add(RefAdapterKit.Name(other, row["NAZIV_DJ"], TranslatedStringKind.Official, Units));
                street.Codes = [new PlaceCode { Scheme = "si-eid", Value = eid }, new PlaceCode { Scheme = "si-ulica", Value = row["SESTAVLJENA_SIFRA"] }];
                street.Type = StreetType.Streetname;
                street.Source = [Units];
                RefAdapterKit.StreetIn(street, municipality.Id, null, settlementByEid.GetValueOrDefault(row["EID_NASELJE"]));
                streetByEid[eid] = id;
            }

            // Postal areas: "Piran - Pirano" holds both names in one string where the area is bilingual.
            foreach (var row in Rows(input, "POSTNI_OKOLISI"))
            {
                var name = row["NAZIV"];
                var parts = name.Split(" - ", 2, StringSplitOptions.TrimEntries);
                postalNames[row["SIFRA"]] = parts.Length == 2 ? (parts[0], parts[1]) : (name, null);
            }
        }

        /// <summary>The second official name of a unit in a bilingual municipality, by its language.</summary>
        private string? SecondLanguage(string municipalityCode, RefCsvRow row)
        {
            if (row["NAZIV_DJ"].Length == 0) return null;
            if (SloveniaProfile.BilingualMunicipalities.TryGetValue(municipalityCode, out var lang)) return lang;
            build.Warnings.Add($"{Units}: '{row["NAZIV"]}' has a second name '{row["NAZIV_DJ"]}' outside the known bilingual municipalities: not imported.");
            return null;
        }

        private void Unit(string id, string kind, PlaceRole role, string parent, RefCsvRow row, string? secondLanguage, string scheme, string code)
        {
            var place = build.Place(id);
            place.Periods = [new PlacePeriod { Kind = kind, Role = role, Parent = parent }];
            place.Names = new TranslatedString { Entries = [RefAdapterKit.Name("sl", row["NAZIV"], TranslatedStringKind.Official, Units)] };
            place.OfficialLanguages = [new DatedLanguage { Lang = "sl" }];
            if (secondLanguage is not null)
            {
                place.Names.Entries.Add(RefAdapterKit.Name(secondLanguage, row["NAZIV_DJ"], TranslatedStringKind.Official, Units));
                place.OfficialLanguages.Add(new DatedLanguage { Lang = secondLanguage });
            }
            place.Codes = [new PlaceCode { Scheme = scheme, Value = code }];
            place.TimeZone = SloveniaProfile.Zone;
            place.Source = [Units];
        }

        public void MapAddresses(SourceInput input)
        {
            foreach (var row in Rows(input, "REGISTER_NASLOVOV"))
            {
                if (!municipalityByEid.TryGetValue(row["EID_OBCINA"], out var municipality))
                    continue;

                // The municipality's statistical region (the units layer states none).
                var place = build.Place(municipality.Id);
                if (regionByEid.TryGetValue(row["EID_STATISTICNA_REGIJA"], out var region) && place.Periods[0].Parent == country)
                    place.Periods[0].Parent = region;
                RefAdapterKit.AddSource(place.Source, Addresses);

                var code = row["POSTNI_OKOLIS_SIFRA"];
                if (code.Length == 0) continue;
                var postalId = build.Ledger.Mint(Addresses, "si-posta:" + code, RefIds.PostalCode("si", code));
                var postal = build.PostalCode(postalId, "si", code);
                var (sl, other) = postalNames.TryGetValue(code, out var names) ? names : (row["POSTNI_OKOLIS_NAZIV"], (string?)null);
                var settlementPlace = build.Places.Values.FirstOrDefault(p => p.Periods.Any(x => x.Role == PlaceRole.Locality && x.Parent == municipality.Id)
                                                                             && p.Names.Entries.Any(e => RefAdapterKit.Key(e.Value) == RefAdapterKit.Key(sl)))?.Id;
                var locality = RefAdapterKit.Locality(postal, sl, "sl", Addresses, municipality.Id, settlementPlace, main: true);
                if (other is not null && SloveniaProfile.BilingualMunicipalities.TryGetValue(municipality.Code, out var lang)
                    && !locality.Names.Entries.Any(e => e.Value == other))
                    locality.Names.Entries.Add(RefAdapterKit.Name(lang, other, TranslatedStringKind.Official, Addresses));

                if (streetByEid.TryGetValue(row["EID_ULICA"], out var streetId))
                {
                    var street = build.Street(streetId, "si");
                    RefAdapterKit.StreetIn(street, municipality.Id, postalId);
                    RefAdapterKit.AddSource(street.Source, Addresses);
                }
            }
        }
    }
}
