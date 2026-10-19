using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Contracts;
using MintPlayer.Spark.ReferenceData.Indexes;
using MintPlayer.Spark.ReferenceData.Search;
using MintPlayer.Spark.ReferenceData.Sources;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.ReferenceData.Api;

/// <summary>
/// The release the database holds, in memory (§4.11): the ETag carries it, so a matching <c>If-None-Match</c> is a 304
/// without a query. Re-read at most every 30 seconds (one document load).
/// </summary>
public sealed class RefDataCurrentRelease(IDocumentStore store, TimeProvider time)
{
    private string release = "none";
    private DateTimeOffset readAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - readAt < TimeSpan.FromSeconds(30))
            return release;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (time.GetUtcNow() - readAt >= TimeSpan.FromSeconds(30))
            {
                using var session = store.OpenAsyncSession();
                release = (await session.LoadAsync<RefDataState>(RefDataState.DocumentId, cancellationToken))?.Release ?? "none";
                readAt = time.GetUtcNow();
            }
        }
        finally
        {
            gate.Release();
        }
        return release;
    }

    /// <summary>Forgets the cached release (after an import or the seed).</summary>
    public void Invalidate() => readAt = DateTimeOffset.MinValue;
}

/// <summary>Composes the <c>/api/v1</c> responses (§4.11) from the typeahead and the documents.</summary>
public sealed class RefApiService(
    IDocumentStore store,
    RefTypeahead typeahead,
    RefDataCurrentRelease currentRelease,
    IEnumerable<IReferenceDataSource> sources,
    TimeProvider time)
{
    /// <summary>The API terms (§4.12): no restriction beyond the upstream licences, plus G-NAF's clause passed on.</summary>
    public const string Terms =
        "Reference data is provided under the licences of its sources, listed with their credit lines at /api/v1/sources. " +
        "Keep those attributions when you pass the data on. No restriction is added beyond the upstream licences; " +
        "rate limits and fair use apply, and bulk users sync releases instead of crawling the API. " +
        "Australian data (G-NAF) may not be used to generate an address or a compilation of addresses for the sending of mail " +
        "unless each address is verified against a second source. Sources are named as sources, not as endorsers.";

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Date(DateOnly? day) => day is { } d ? Date(d) : null;

    public IReadOnlyList<string> AttributionFor(IEnumerable<string?> countries)
    {
        var set = countries.Where(c => c is not null).Select(c => c!.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return [.. sources.Where(s => s.Country == "*" || set.Contains(s.Country)).Select(s => s.Licence.Compact).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal)];
    }

    public async Task<SuggestResponse> SuggestAsync(SuggestQuery query, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var ranked = await typeahead.SuggestAsync(session, query, cancellationToken);
        var places = await session.LoadAsync<RefPlace>(ranked.SelectMany(r => new[] { r.Entry.Place, r.Entry.Municipality, r.Entry.Type == "place" ? r.Entry.DocumentId : null }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var postal = await session.LoadAsync<RefPostalCode>(ranked.Where(r => r.Entry.Type == "postal-locality").Select(r => r.Entry.DocumentId).Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var context = new Names(session, query.AsOf, query.Lang);
        await context.PreloadAsync(places.Values.OfType<RefPlace>(), cancellationToken);

        var items = new List<SuggestItem>();
        foreach (var r in ranked)
        {
            var e = r.Entry;
            if (e.Type == "postal-locality" && postal.GetValueOrDefault(e.DocumentId) is { } code && code.Localities.FirstOrDefault(l => l.Key == e.LocalityKey) is { } locality)
            {
                var municipality = e.Municipality is null ? null : places.GetValueOrDefault(e.Municipality);
                var name = locality.Names.Resolve([query.Lang], asOf: query.AsOf);
                var municipalityName = municipality is null ? null : context.Name(municipality);
                var path = municipality is null ? [] : context.Path(municipality, includeSelf: false);
                items.Add(new SuggestItem(
                    "postal-locality",
                    $"{code.Code} {name}",
                    string.Join(", ", new[] { municipalityName }.Concat(path.Where(p => p.Kind.EndsWith(":province", StringComparison.Ordinal)).Select(p => p.Name)).Where(s => !string.IsNullOrEmpty(s) && s != name)),
                    Match(name, query.Q, r.Fuzzy, locality.Names, query.Lang),
                    new SuggestPostalCode(code.Id!, code.Code),
                    new SuggestLocality(locality.Key, name, locality.Place),
                    e.Place is null ? null : new SuggestRef(e.Place, places.GetValueOrDefault(e.Place) is { } p ? context.Name(p) : name),
                    municipality is null ? null : new SuggestRef(municipality.Id!, municipalityName!),
                    path,
                    municipality?.TimeZone,
                    municipality is null || municipality.Lifetime.Contains(query.AsOf) ? "current" : "ended",
                    municipality is null ? [] : context.Successors(municipality),
                    code.Country));
            }
            else if (e.Type == "place" && places.GetValueOrDefault(e.DocumentId) is { } place)
            {
                var name = context.Name(place);
                var path = context.Path(place, includeSelf: false);
                var municipality = context.MunicipalityOf(place);
                items.Add(new SuggestItem(
                    "place",
                    name,
                    string.Join(", ", path.Take(2).Select(p => p.Name)),
                    Match(name, query.Q, r.Fuzzy, place.Names, query.Lang),
                    null,
                    null,
                    new SuggestRef(place.Id!, name),
                    municipality is null ? null : new SuggestRef(municipality.Id!, context.Name(municipality)),
                    path,
                    place.TimeZone,
                    place.Lifetime.Contains(query.AsOf) ? "current" : "ended",
                    context.Successors(place),
                    place.Country));
            }
        }

        DateOnly? since = null;
        if (query.Country is { } cc && await session.LoadAsync<RefCountry>(RefIds.CountryId(cc), cancellationToken) is { } country)
            since = country.Profile.HistoryKnownSince;
        return new SuggestResponse(await currentRelease.GetAsync(cancellationToken), Date(query.AsOf), AttributionFor(items.Select(i => i.Country).Append(query.Country)), items, Date(since));
    }

    public async Task<StreetSuggestResponse> StreetsAsync(StreetQuery query, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var ranked = await typeahead.StreetsAsync(session, query, cancellationToken);
        var streets = await session.LoadAsync<RefStreet>(ranked.Select(r => r.Entry.DocumentId), cancellationToken);
        var municipalities = await session.LoadAsync<RefPlace>(ranked.Select(r => r.Entry.Municipality).Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var context = new Names(session, query.AsOf, query.Lang);
        var items = new List<StreetSuggestItem>();
        foreach (var r in ranked)
        {
            if (streets.GetValueOrDefault(r.Entry.DocumentId) is not { } street) continue;
            var municipality = municipalities.GetValueOrDefault(r.Entry.Municipality);
            var languages = municipality is null ? [] : context.LanguagesOf(municipality);
            var label = PlaceNames.Display(street.Names, languages, query.Lang, query.AsOf).Display;
            items.Add(new StreetSuggestItem(
                street.Id!, label, street.Qualifier, street.Type.ToString().ToLowerInvariant(),
                Match(label, query.Q, r.Fuzzy, street.Names, query.Lang),
                new SuggestRef(r.Entry.Municipality, municipality is null ? r.Entry.Municipality : context.Name(municipality)),
                r.Entry.PostalCodes, r.Fuzzy));
        }
        return new StreetSuggestResponse(await currentRelease.GetAsync(cancellationToken), Date(query.AsOf), AttributionFor(streets.Values.Select(s => s?.Country)), items);
    }

    public async Task<PlaceResponse> PlaceAsync(string id, DateOnly asOf, string? lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await PlaceAsync(session, id, asOf, lang, cancellationToken);
    }

    public async Task<ResolveResponse> ResolveAsync(IReadOnlyList<string> ids, DateOnly asOf, string? lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var items = new List<PlaceResponse>();
        foreach (var id in ids)
            items.Add(await PlaceAsync(session, id, asOf, lang, cancellationToken));
        return new ResolveResponse(await currentRelease.GetAsync(cancellationToken), Date(asOf), AttributionFor(ids.Select(RefIds.CountryOf)), items);
    }

    public async Task<ByCodeResponse> ByCodeAsync(string country, string scheme, string code, DateOnly asOf, string? lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var key = DateKeys.Of(asOf);
        // The index's documents (not projections): a place that held the code in two periods comes back once per entry.
        var matches = await session.Advanced.AsyncDocumentQuery<RefPlace>(RefDataIndexes.ByCode)
            .WhereEquals(nameof(PlaceCodeEntry.Country), country)
            .AndAlso().WhereEquals(nameof(PlaceCodeEntry.Scheme), scheme)
            .AndAlso().WhereEquals(nameof(PlaceCodeEntry.Value), code)
            .AndAlso().WhereLessThanOrEqual(nameof(PlaceCodeEntry.FromKey), key)
            .AndAlso().WhereGreaterThan(nameof(PlaceCodeEntry.ToKey), key)
            .Take(20).ToListAsync(cancellationToken);
        var context = new Names(session, asOf, lang);
        var items = new List<PlaceItem>();
        foreach (var place in matches.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            await context.PreloadAsync([place], cancellationToken);
            items.Add(context.Item(place));
        }
        return new ByCodeResponse(await currentRelease.GetAsync(cancellationToken), Date(asOf), AttributionFor([country]), items);
    }

    public async Task<PostalCodeResponse> PostalCodeAsync(string country, string code, string? lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var asOf = Today;
        var id = RefIds.PostalCode(country, code);
        var postal = await session.LoadAsync<RefPostalCode>(id, cancellationToken);
        var localities = new List<PostalLocalityItem>();
        if (postal is not null)
        {
            var municipalities = await session.LoadAsync<RefPlace>(postal.Localities.SelectMany(l => l.Municipalities.Select(m => m.Id)).Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
            var context = new Names(session, asOf, lang);
            foreach (var locality in postal.Localities)
            {
                var link = locality.Municipalities.FirstOrDefault(m => DateKeys.Covers(m.FromKey, m.ToKey, asOf));
                var municipality = link is null ? null : municipalities.GetValueOrDefault(link.Id);
                localities.Add(new PostalLocalityItem(locality.Key, locality.Names.Resolve([lang], asOf: asOf), locality.Place,
                    municipality is null ? null : new SuggestRef(municipality.Id!, context.Name(municipality)), locality.Main));
            }
        }
        return new PostalCodeResponse(await currentRelease.GetAsync(cancellationToken), AttributionFor([country]), postal?.Id, code, localities);
    }

    public async Task<CountriesResponse> CountriesAsync(string? lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var countries = await session.Query<RefCountry>().Take(512).ToListAsync(cancellationToken);
        var withStreets = (await session.Query<RefStreet>().Select(s => s.Country).Distinct().Take(512).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = countries.OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new CountryItem(c.Code, CldrNames.Territory(c.Code, lang), c.Profile.TimeZone, withStreets.Contains(c.Code)))
            .ToList();
        return new CountriesResponse(await currentRelease.GetAsync(cancellationToken), [.. AttributionFor(countries.Select(c => c.Code)), "Unicode CLDR (Unicode License v3)"], items);
    }

    public async Task<CountryProfileResponse?> ProfileAsync(string country, string? lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<RefCountry>(RefIds.CountryId(country), cancellationToken) is not { } c)
            return null;
        var p = c.Profile;
        return new CountryProfileResponse(
            await currentRelease.GetAsync(cancellationToken), c.Code,
            [.. p.Levels.OrderBy(l => l.Order).Select(l => new AdminLevelItem(l.Key, l.Role.ToString().ToLowerInvariant(), l.Order, l.Optional, l.Labels.Resolve([lang])))],
            p.MunicipalityLevel,
            [.. p.OfficialLanguages.Where(l => l.To is null).Select(l => l.Lang)],
            p.AddressLines, p.PostcodePattern, p.HouseNumbers?.NumberPattern, p.HouseNumbers?.BoxPattern, p.TypeaheadSteps,
            Date(p.HistoryKnownSince), p.TimeZone,
            [.. p.VatTerritories.Select(t => new VatTerritoryItem(t.Code, t.Names.Resolve([lang]), t.Treatment.ToString(), t.PostcodePattern, t.LegalBasis, t.Note))]);
    }

    /// <summary>IANA id → CLDR exemplar city in <paramref name="lang"/>, only where CLDR states one (an absent zone falls back in the caller, D36).</summary>
    public async Task<TimeZonesResponse> TimeZonesAsync(string lang, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var zones = await session.Query<RefTimeZone>().Take(1024).ToListAsync(cancellationToken);
        var chain = LanguageTag.LookupChain(lang).ToList();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var zone in zones.OrderBy(z => z.Zone, StringComparer.Ordinal))
            foreach (var candidate in chain)
                if (zone.Names.Entries.FirstOrDefault(e => LanguageTag.AreEqual(e.Tag, candidate)) is { } entry)
                {
                    names[zone.Zone] = entry.Value;
                    break;
                }
        return new TimeZonesResponse(await currentRelease.GetAsync(cancellationToken), lang, ["Unicode CLDR (Unicode License v3)"], names);
    }

    public async Task<SourcesResponse> SourcesAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var states = await session.LoadAsync<SourceState>(sources.Select(s => SourceState.IdFor(s.Id)), cancellationToken);
        var items = sources.OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new SourceItem(s.Id, s.Country, s.Licence.Name, s.Licence.Licence, s.Licence.LicenceUrl, s.Licence.Attribution,
                states.GetValueOrDefault(SourceState.IdFor(s.Id))?.Version, s.Licence.Modified))
            .ToList();
        return new SourcesResponse(await currentRelease.GetAsync(cancellationToken), items, Terms);
    }

    private async Task<PlaceResponse> PlaceAsync(IAsyncDocumentSession session, string id, DateOnly asOf, string? lang, CancellationToken cancellationToken)
    {
        var place = await session.LoadAsync<RefPlace>(id, cancellationToken);
        var release = await currentRelease.GetAsync(cancellationToken);
        if (place is null)
            return new PlaceResponse(release, Date(asOf), [], null, [], false, false);
        var context = new Names(session, asOf, lang);
        await context.PreloadAsync([place], cancellationToken);
        var country = await session.LoadAsync<RefCountry>(RefIds.CountryId(place.Country), cancellationToken);
        var result = await RefAsOf.ResolveAsync(place, asOf, async next => await session.LoadAsync<RefPlace>(next, cancellationToken), country?.Profile.HistoryKnownSince);
        await context.PreloadAsync(result.Places, cancellationToken);
        return new PlaceResponse(release, Date(asOf), AttributionFor([place.Country]), context.Item(place),
            [.. result.Places.Select(context.Item)], result.Ambiguous, result.BeforeHistory);
    }

    /// <summary>A match with the character ranges of the folded query inside the label (case-insensitive, ordinal).</summary>
    private static SuggestMatch Match(string label, string q, bool fuzzy, TranslatedString names, string? lang)
    {
        var entry = names.Entries.FirstOrDefault(e => string.Equals(e.Value, label, StringComparison.Ordinal))
                    ?? names.Entries.FirstOrDefault(e => LanguageTag.AreEqual(e.Tag, lang)) ?? names.Entries.FirstOrDefault();
        var start = label.IndexOf(q.Trim(), StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<IReadOnlyList<int>> ranges = start >= 0 && !fuzzy ? [[start, start + q.Trim().Length]] : [];
        return new SuggestMatch(label, entry?.Tag ?? string.Empty, entry?.Kind?.ToString().ToLowerInvariant(), ranges, fuzzy);
    }

    /// <summary>Names, paths and items of places at one date in one language, with every ancestor loaded once.</summary>
    private sealed class Names(IAsyncDocumentSession session, DateOnly asOf, string? lang)
    {
        private readonly Dictionary<string, RefPlace> loaded = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RefCountry?> countries = new(StringComparer.OrdinalIgnoreCase);

        public async Task PreloadAsync(IEnumerable<RefPlace> places, CancellationToken cancellationToken)
        {
            var list = places.ToList();
            foreach (var p in list) loaded[p.Id!] = p;
            var ancestors = list.SelectMany(p => p.Segments.SelectMany(s => s.Ancestors))
                .Concat(list.SelectMany(p => p.SupersededBy.Concat(p.Supersedes).Select(e => e.Id)))
                .Where(a => !loaded.ContainsKey(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ancestors.Count > 0)
                foreach (var (id, place) in await session.LoadAsync<RefPlace>(ancestors, cancellationToken))
                    if (place is not null) loaded[id] = place;
            foreach (var cc in list.Select(p => p.Country).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !countries.ContainsKey(c)))
                countries[cc] = await session.LoadAsync<RefCountry>(RefIds.CountryId(cc), cancellationToken);
        }

        public IReadOnlyList<string> LanguagesOf(RefPlace place)
        {
            var own = place.OfficialLanguages.Where(l => (l.From is null || l.From <= asOf) && (l.To is null || asOf < l.To)).Select(l => l.Lang).ToList();
            if (own.Count > 0) return own;
            return countries.GetValueOrDefault(place.Country)?.Profile.OfficialLanguages.Select(l => l.Lang).ToList() ?? [];
        }

        public string Name(RefPlace place) => PlaceNames.Display(place.Names, LanguagesOf(place), lang, asOf).Display;

        public List<SuggestPathItem> Path(RefPlace place, bool includeSelf)
        {
            var segment = RefAsOf.SegmentAt(place, asOf) ?? place.Segments.LastOrDefault();
            var path = new List<SuggestPathItem>();
            if (includeSelf)
                path.Add(new SuggestPathItem(place.Id!, segment?.Kind ?? string.Empty, Name(place)));
            foreach (var ancestor in segment?.Ancestors ?? [])
                if (loaded.TryGetValue(ancestor, out var a))
                    path.Add(new SuggestPathItem(a.Id!, (RefAsOf.SegmentAt(a, asOf) ?? a.Segments.LastOrDefault())?.Kind ?? string.Empty, Name(a)));
            return path;
        }

        public RefPlace? MunicipalityOf(RefPlace place)
        {
            var segment = RefAsOf.SegmentAt(place, asOf) ?? place.Segments.LastOrDefault();
            if (segment?.Role == PlaceRole.Municipality) return place;
            foreach (var ancestor in segment?.Ancestors ?? [])
                if (loaded.TryGetValue(ancestor, out var a) && (RefAsOf.SegmentAt(a, asOf) ?? a.Segments.LastOrDefault())?.Role == PlaceRole.Municipality)
                    return a;
            return null;
        }

        public List<SuggestRef> Successors(RefPlace place)
            => [.. place.SupersededBy.Where(e => e.Date <= asOf)
                .Select(e => new SuggestRef(e.Id, loaded.TryGetValue(e.Id, out var s) ? Name(s) : e.Id))];

        public PlaceItem Item(RefPlace place)
        {
            var display = PlaceNames.Display(place.Names, LanguagesOf(place), lang, asOf);
            var period = RefAsOf.PeriodAt(place, asOf) ?? place.Periods.LastOrDefault();
            SuccessionItem Edge(SuccessionEdge e) => new(e.Id, loaded.TryGetValue(e.Id, out var p) ? Name(p) : e.Id, Date(e.Date), e.Event, e.Share.ToString().ToLowerInvariant());
            return new PlaceItem(
                place.Id!, place.Country, display.Display, display.Local, display.Ui, display.Latin,
                period?.Kind, period?.Role.ToString().ToLowerInvariant(), Path(place, includeSelf: false),
                [.. place.Codes.Select(c => new PlaceCodeItem(c.Scheme, c.Value, Date(c.From), Date(c.To)))],
                place.TimeZone, place.Lifetime.Contains(asOf) ? "current" : "ended",
                [.. place.SupersededBy.Select(Edge)], [.. place.Supersedes.Select(Edge)],
                Date(place.Lifetime.From), Date(place.Lifetime.To));
        }
    }
}
