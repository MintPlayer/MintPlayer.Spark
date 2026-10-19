using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Best;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Flemish;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Readers;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium;

/// <summary>The inputs of the BeST bundle and the Flemish sources, parsed once (plan R6).</summary>
internal sealed class RestInputs
{
    public List<BestRecord> Best { get; } = [];
    public List<FlemishMunicipalityMapping> DiffMunicipalities { get; } = [];
    public List<FlemishStreetMapping> DiffStreets { get; } = [];
    public List<FlemishApiMunicipality> ApiMunicipalities { get; } = [];
    public List<FlemishApiStreet> ApiStreets { get; } = [];

    public static RestInputs From(IReadOnlyDictionary<string, SourceInput> inputs)
    {
        var rest = new RestInputs();
        if (inputs.TryGetValue(BelgiumBestSource.SourceId, out var best))
            foreach (var file in best.Files.Where(f => BelgiumBestSource.InnerXml().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                using var stream = file.Open();
                rest.Best.AddRange(BestXmlReader.Read(stream));
            }
        if (inputs.TryGetValue(FlemishDiffSource.SourceId, out var diff))
        {
            if (diff.Find(FlemishDiffSource.MunicipalityFile()) is { } municipalities)
                using (var stream = municipalities.Open())
                    rest.DiffMunicipalities.AddRange(FlemishDiffReader.Municipalities(stream));
            if (diff.Find(FlemishDiffSource.StreetFile()) is { } streets)
                using (var stream = streets.Open())
                    rest.DiffStreets.AddRange(FlemishDiffReader.Streets(stream));
        }
        if (inputs.TryGetValue(FlemishApiSource.SourceId, out var api))
            foreach (var file in api.Files.Where(f => FlemishApiSource.Files().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                using var stream = file.Open();
                switch (FlemishApiReader.Read(stream))
                {
                    case FlemishApiMunicipality m: rest.ApiMunicipalities.Add(m); break;
                    case FlemishApiStreet s: rest.ApiStreets.Add(s); break;
                }
            }
        return rest;
    }
}

internal sealed partial class BelgiumMapper
{
    private sealed partial class Run
    {
        /// <summary>The 2025 Flemish mergers' legal date, used only when REFNIS does not state it (a warning says so).</summary>
        private static readonly DateOnly Fusion2025 = new(2025, 1, 1);

        private static readonly Dictionary<string, List<string>> RegionLanguages = new()
        {
            ["vlg"] = ["nl"],
            ["bru"] = ["fr", "nl"],
            ["wal"] = ["fr"],
        };

        public RestInputs Rest { get; init; } = new();

        private void MapRest()
        {
            MapBest();
            MapFlemishDiff();
            MapFlemishApi();
        }

        /// <summary>The place a NIS code minted, without minting one (lookups must not invent units).</summary>
        private RefPlace? Existing(string nis)
            => Target.Ledger.Find("nis:" + nis) is { } id && Target.Places.TryGetValue(id, out var place) ? place : null;

        // ---- BeST full bundle (be-best) ----

        /// <summary>
        /// Adds what the postal-streets file lacks: homonym additions (<c>Antverpiastraat (HO)</c>), the
        /// <c>hamlet</c> type, and current streets no postal code links (with their municipality, no postal code).
        /// Retired, proposed, reserved and rejected streets are never added from BeST (BeST is current state only; the
        /// Flemish sources date the retired ones). Municipalities, postal infos and Walloon parts are checked against
        /// the build: a BeST unit REFNIS or the postal-streets file does not know is listed as a warning.
        /// </summary>
        private void MapBest()
        {
            foreach (var record in Rest.Best)
            {
                switch (record.Kind)
                {
                    case "streetName": MapBestStreet(record); break;
                    case "municipality":
                        if (Existing(record.ObjectId) is null)
                            Target.Warnings.Add($"be-best: municipality {record.ObjectId} ({record.Region}) has no REFNIS place.");
                        break;
                    case "postalInfo": MapBestPostalInfo(record); break;
                    case "partOfMunicipality":
                        if (Target.Ledger.Find("best-wal-part:" + record.ObjectId) is null)
                            Target.Warnings.Add($"be-best: part of municipality {record.ObjectId} ({FirstName(record)}) has no street in the postal-streets file, so nothing links it to its municipality (BeST states no parent).");
                        break;
                }
            }
        }

        private void MapBestStreet(BestRecord record)
        {
            var region = BestNamespace.StreetRegion(record.Namespace) ?? BestNamespace.Region(record.Region);
            if (region is null)
            {
                Target.Warnings.Add($"be-best: street {record.ObjectId} has an unknown namespace '{record.Namespace}'.");
                return;
            }
            var identity = $"best-{region}:{record.ObjectId}";
            RefStreet? street = Target.Ledger.Find(identity) is { } known && Target.Streets.TryGetValue(known, out var found) ? found : null;
            if (street is null)
            {
                if (record.Status != "current" || record.Municipality is null || Existing(record.Municipality) is not { } municipality)
                    return;
                var id = Target.Ledger.Mint(BelgiumBestSource.SourceId, identity, RefIds.Street("be", region, record.ObjectId));
                street = Target.Street(id, "be");
                var languages = GermanSpeaking.Contains(record.Municipality) ? ["de"] : RegionLanguages[region];
                foreach (var (lang, value) in record.Names.OrderBy(n => languages.IndexOf(n.Lang) is var i and >= 0 ? i : 9))
                    if (value.Length > 0 && lang.Length > 0)
                        street.Names.Entries.Add(RefAdapterKit.Name(lang, value, languages.Contains(lang) ? TranslatedStringKind.Official : TranslatedStringKind.Exonym, BelgiumBestSource.SourceId));
                street.Codes = [new PlaceCode { Scheme = "best-" + region, Value = record.ObjectId }];
                street.Periods = [new StreetPeriod { Municipality = municipality.Id! }];
            }
            if (record.HomonymAddition is { Length: > 0 } homonym)
                street.Qualifier ??= homonym;
            if (record.Type == "hamlet")
                street.Type = StreetType.Hamlet;
            RefAdapterKit.AddSource(street.Source, BelgiumBestSource.SourceId);
        }

        /// <summary>A postal code no street links to still gets its document (with the Flemish postal name where one is published).</summary>
        private void MapBestPostalInfo(BestRecord record)
        {
            var code = record.ObjectId;
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[1-9][0-9]{3}$"))
            {
                Target.Warnings.Add($"be-best: postal info '{code}' is not a Belgian postal code.");
                return;
            }
            if (Target.Ledger.Find("postcode:" + code, "be") is { } existing && Target.PostalCodes.ContainsKey(existing))
                return;
            var id = Target.Ledger.Mint(BelgiumBestSource.SourceId, "postcode:" + code, RefIds.PostalCode("be", code));
            var postal = Target.PostalCode(id, "be", code);
            foreach (var (lang, value) in record.Names.Where(n => n.Value.Length > 0))
                RefAdapterKit.Locality(postal, value == value.ToUpperInvariant() ? TitleCase(value) : value, lang.Length > 0 ? lang : "nl", BelgiumBestSource.SourceId, municipality: null, main: value == value.ToUpperInvariant());
            RefAdapterKit.AddSource(postal.Source, BelgiumBestSource.SourceId);
        }

        private static string FirstName(BestRecord record) => record.Names.Count > 0 ? record.Names[0].Value : "?";

        // ---- Flemish 2025 merger diff files (be-flemish-diff) ----

        /// <summary>
        /// Street identity across the Flemish re-identification (Q-R2): a 1 → 1 mapping <b>continues</b> the street (the
        /// same document; the old id becomes an alias in the ledger and a code dated until the merger; a period in the
        /// old municipality until the merger; a renamed street keeps its old name as <c>historic</c>). Several old ids
        /// into one new id is a <b>merge</b>, one old id into several new ids a <b>split</b>: the old streets become
        /// documents of their own, ended on the merger date, with a lineage event. The date is the old municipality's
        /// REFNIS end (Statbel is the primary source, Q-R3), never the file's date.
        /// </summary>
        private void MapFlemishDiff()
        {
            if (Rest.DiffStreets.Count == 0 && Rest.DiffMunicipalities.Count == 0)
                return;
            var continuing = Rest.DiffMunicipalities.Where(m => m.OldNis == m.NewNis).Select(m => m.NewNis).ToHashSet(StringComparer.Ordinal);
            foreach (var municipality in Rest.DiffMunicipalities.Where(m => m.OldNis != m.NewNis))
                if (Existing(municipality.OldNis) is { } old && old.Periods.LastOrDefault()?.To is null)
                    Target.Warnings.Add($"be-flemish-diff: {municipality.OldNis} {municipality.OldName} merges into {municipality.NewNis}, but REFNIS has it open.");

            var byNew = Rest.DiffStreets.GroupBy(s => s.NewId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byOld = Rest.DiffStreets.GroupBy(s => s.OldId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var row in Rest.DiffStreets.OrderBy(r => r.NewId, StringComparer.Ordinal).ThenBy(r => r.OldId, StringComparer.Ordinal))
            {
                var region = BestNamespace.StreetRegion(row.NewNamespace) ?? "vlg";
                var date = MergerDate(row.OldNis, row.NewNis);
                if (Target.Ledger.Find($"best-{region}:{row.NewId}") is not { } newId || !Target.Streets.TryGetValue(newId, out var current))
                {
                    Target.Warnings.Add($"be-flemish-diff: street {row.NewId} ({row.NewName}) is not in the build; its predecessor {row.OldId} is skipped.");
                    continue;
                }
                var oldPlace = Existing(row.OldNis);
                var newPlace = Existing(row.NewNis);
                if (oldPlace is null)
                {
                    Target.Warnings.Add($"be-flemish-diff: municipality {row.OldNis} ({row.OldName}) is not in the build; street {row.OldId} is skipped.");
                    continue;
                }

                if (byNew[row.NewId].Count == 1 && byOld[row.OldId].Count == 1)
                    ContinueStreet(row, region, date, current, oldPlace, newPlace, continuing.Contains(row.NewNis));
                else
                    EndStreet(row, region, date, current, oldPlace);
            }

            // One lineage event per merged or split street (the old documents end; the new ones start).
            foreach (var (newStreet, rows) in byNew.Where(g => g.Value.Count > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
                AddStreetLineage(rows, LineageKind.Merge, rows[0].NewId);
            foreach (var (oldStreet, rows) in byOld.Where(g => g.Value.Count > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
                AddStreetLineage(rows, LineageKind.Split, rows[0].OldId);
        }

        private DateOnly MergerDate(string oldNis, string newNis)
        {
            if (units.TryGetValue(oldNis, out var rows) && rows[^1].To is { } end)
                return end;
            if (oldNis != newNis && units.TryGetValue(newNis, out var created) && created[0].From is var start && start.Year > 1970)
                return start;
            Target.Warnings.Add($"be-flemish-diff: REFNIS states no end for {oldNis}; the 2025 merger date is assumed.");
            return Fusion2025;
        }

        private void ContinueStreet(FlemishStreetMapping row, string region, DateOnly date, RefStreet street, RefPlace oldPlace, RefPlace? newPlace, bool targetContinues)
        {
            Target.Ledger.Alias(FlemishDiffSource.SourceId, $"best-{region}:{row.OldId}", street.Id!);
            if (!street.Codes.Any(c => c.Value == row.OldId && c.Scheme == "best-" + region))
                street.Codes.Add(new PlaceCode { Scheme = "best-" + region, Value = row.OldId, To = date });

            // A street of a newly created municipality starts on the merger date; one that joins a continuing
            // municipality (Borsbeek's streets into Antwerpen, possibly an existing Antwerpen street) keeps its start.
            var postalCodes = street.Periods.Where(p => p.To is null).SelectMany(p => p.PostalCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!targetContinues)
            {
                foreach (var code in street.Codes.Where(c => c.Value == row.NewId && c.From is null))
                    code.From = date;
                if (newPlace is not null)
                    foreach (var period in street.Periods.Where(p => p.From is null && p.To is null && string.Equals(p.Municipality, newPlace.Id, StringComparison.OrdinalIgnoreCase)))
                        period.From = date;
            }
            if (!street.Periods.Any(p => string.Equals(p.Municipality, oldPlace.Id, StringComparison.OrdinalIgnoreCase) && p.To == date))
                // The postal codes before the merger are assumed unchanged (the diff file states none): a merger re-identifies
                // streets, it does not re-route mail.
                street.Periods.Add(new StreetPeriod { Municipality = oldPlace.Id!, To = date, PostalCodes = [.. postalCodes] });

            if (!string.Equals(row.OldName, row.NewName, StringComparison.Ordinal) && row.OldName.Length > 0)
            {
                // A renamed homonym (Kortessem's Molenstraat → Coenegrachtsmolenstraat): the old name stays searchable.
                street.Names.Entries = [.. street.Names.Entries.Select(e => e.Kind == TranslatedStringKind.Official && e.ValidFrom is null ? e with { ValidFrom = date } : e)];
                if (!street.Names.Entries.Any(e => e.Value == row.OldName && e.Kind == TranslatedStringKind.Historic))
                    street.Names.Entries.Add(RefAdapterKit.Name("nl", row.OldName, TranslatedStringKind.Historic, FlemishDiffSource.SourceId, to: date));
            }
            if (row.NewHomonym.Length > 0)
                street.Qualifier ??= row.NewHomonym;
            RefAdapterKit.AddSource(street.Source, FlemishDiffSource.SourceId);
        }

        /// <summary>An old street of a merge or split: a document of its own, ended on the merger date.</summary>
        private void EndStreet(FlemishStreetMapping row, string region, DateOnly date, RefStreet successor, RefPlace oldPlace)
        {
            var id = Target.Ledger.Mint(FlemishDiffSource.SourceId, $"best-{region}:{row.OldId}", RefIds.Street("be", region, row.OldId));
            var old = Target.Street(id, "be");
            if (old.Names.Entries.Count == 0)
            {
                old.Names.Entries.Add(RefAdapterKit.Name("nl", row.OldName, TranslatedStringKind.Official, FlemishDiffSource.SourceId, to: date));
                old.Codes = [new PlaceCode { Scheme = "best-" + region, Value = row.OldId, To = date }];
                old.Qualifier = row.OldHomonym.Length > 0 ? row.OldHomonym : null;
                old.Periods = [new StreetPeriod { Municipality = oldPlace.Id!, To = date }];
            }
            RefAdapterKit.AddSource(old.Source, FlemishDiffSource.SourceId);
            RefAdapterKit.AddSource(successor.Source, FlemishDiffSource.SourceId);
        }

        private void AddStreetLineage(List<FlemishStreetMapping> rows, LineageKind kind, string anchor)
        {
            var region = BestNamespace.StreetRegion(rows[0].NewNamespace) ?? "vlg";
            var date = MergerDate(rows[0].OldNis, rows[0].NewNis);
            var lineage = new RefLineageEvent
            {
                Id = RefIds.LineageEvent("be", date, kind, "best-" + region, anchor),
                Country = "be",
                Date = date,
                Kind = kind,
                Sources = [new LineageSource { Source = FlemishDiffSource.SourceId, Date = date, Note = $"{string.Join(", ", rows.Select(r => r.OldId).Distinct())} → {string.Join(", ", rows.Select(r => r.NewId).Distinct())}" }],
            };
            foreach (var row in rows)
            {
                var oldId = Target.Ledger.Find($"best-{region}:{row.OldId}");
                var newId = Target.Ledger.Find($"best-{region}:{row.NewId}");
                if (oldId is null || newId is null) continue;
                lineage.Operations.Add(new LineageOperation { Place = oldId, Op = LineageOp.End, Into = newId, Share = kind == LineageKind.Split ? SuccessionShare.Part : SuccessionShare.Whole });
                if (!lineage.Operations.Any(o => o.Place == newId && o.Op == LineageOp.Create))
                    lineage.Operations.Add(new LineageOperation { Place = newId, Op = LineageOp.Create });
            }
            if (lineage.Operations.Count > 0)
                Target.AddLineage(lineage);
        }

        // ---- Flemish Basisregisters API (be-flemish-api) ----

        /// <summary>
        /// Official languages and facility languages per Flemish municipality (the API is the authority for them), and
        /// the retired street names of merged-away municipalities that no diff file dates (the 2019 mergers): a retired
        /// street is ended with its municipality. A municipality the API calls retired while REFNIS has it open is listed.
        /// </summary>
        private void MapFlemishApi()
        {
            foreach (var municipality in Rest.ApiMunicipalities)
            {
                if (Existing(municipality.Nis) is not { } place)
                    continue;
                if (municipality.OfficialLanguages.Count > 0
                    && !place.OfficialLanguages.Select(l => l.Lang).Order(StringComparer.Ordinal).SequenceEqual(municipality.OfficialLanguages.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    Target.Warnings.Add($"be-flemish-api: {municipality.Nis} has official languages {string.Join("/", municipality.OfficialLanguages)} (was {string.Join("/", place.OfficialLanguages.Select(l => l.Lang))}).");
                    place.OfficialLanguages = [.. municipality.OfficialLanguages.Select(l => new DatedLanguage { Lang = l })];
                }
                // Facility languages (Voeren: fr) are not official: kept as data for forms and mail, never for display.
                foreach (var lang in municipality.FacilityLanguages)
                    if (!place.Codes.Any(c => c.Scheme == "facility-language" && c.Value == lang))
                        place.Codes.Add(new PlaceCode { Scheme = "facility-language", Value = lang });
                if (municipality.Retired && place.Periods.LastOrDefault()?.To is null)
                    Target.Warnings.Add($"be-flemish-api: {municipality.Nis} is retired in the Flemish register but open in REFNIS.");
                RefAdapterKit.AddSource(place.Source, FlemishApiSource.SourceId);
            }

            foreach (var api in Rest.ApiStreets)
            {
                var identity = "best-vlg:" + api.Id;
                if (Target.Ledger.Find(identity) is { } known && Target.Streets.TryGetValue(known, out var street))
                {
                    if (api.Homonyms.Count > 0)
                        street.Qualifier ??= api.Homonyms[0];
                    RefAdapterKit.AddSource(street.Source, FlemishApiSource.SourceId);
                    continue;
                }
                if (!api.Retired || Existing(api.Municipality) is not { } place || place.Periods.LastOrDefault()?.To is not { } end)
                    continue;
                var id = Target.Ledger.Mint(FlemishApiSource.SourceId, identity, RefIds.Street("be", "vlg", api.Id));
                var retired = Target.Street(id, "be");
                foreach (var (lang, value) in api.Names.Where(n => n.Value.Length > 0))
                    retired.Names.Entries.Add(RefAdapterKit.Name(lang, value, lang == "nl" ? TranslatedStringKind.Official : TranslatedStringKind.Exonym, FlemishApiSource.SourceId, to: end));
                retired.Codes = [new PlaceCode { Scheme = "best-vlg", Value = api.Id, To = end }];
                retired.Qualifier = api.Homonyms.Count > 0 ? api.Homonyms[0] : null;
                retired.Periods = [new StreetPeriod { Municipality = place.Id!, To = end }];
                retired.Source = [FlemishApiSource.SourceId];
            }
        }
    }
}
