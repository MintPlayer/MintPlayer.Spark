using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Releases;

namespace MintPlayer.Spark.ReferenceData.Build;

/// <summary>
/// The derive pass (PRD §4.5 step 9, §4.2 "Derived by the build, never authored"): lineage edges, lifetimes,
/// segments cut at every ancestor change with their ancestor chains, integer date keys, search keys, rank, the
/// ordering of set-like arrays, and finally each document's <c>Hash</c>. Deterministic: the same build gives the same
/// bytes (RS10). An ancestor's change re-cuts its descendants' segments here, without editing their periods.
/// </summary>
public static class RefDerivation
{
    /// <summary>Rank = kind weight × this + population (capped below it).</summary>
    public const long RankScale = 1_000_000_000L;

    public static void Derive(RefDataBuild build)
    {
        DeriveEdges(build);
        var segments = new Dictionary<string, List<PlaceSegment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var place in build.Places.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            place.Segments = SegmentsOf(build, place, segments, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var place in build.Places.Values)
        {
            place.Periods = [.. place.Periods.OrderBy(p => DateKeys.From(p.From))];
            place.Lifetime = new DateRange
            {
                From = place.Periods.Count == 0 || place.Periods.Any(p => p.From is null) ? null : place.Periods.Min(p => p.From),
                To = place.Periods.Count == 0 || place.Periods.Any(p => p.To is null) ? null : place.Periods.Max(p => p.To),
            };
            place.Codes = [.. place.Codes.OrderBy(c => c.Scheme, StringComparer.Ordinal).ThenBy(c => DateKeys.From(c.From)).ThenBy(c => c.Value, StringComparer.Ordinal)];
            foreach (var code in place.Codes)
            {
                code.FromKey = DateKeys.From(code.From);
                code.ToKey = DateKeys.To(code.To);
            }
            place.OfficialLanguages = [.. place.OfficialLanguages.OrderBy(l => DateKeys.From(l.From))];
            place.Supersedes = SortEdges(place.Supersedes);
            place.SupersededBy = SortEdges(place.SupersededBy);
            place.Source = [.. place.Source.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)];
            place.Search = RefSearchKeys.For(place.Names.Entries.Select(e => e.Value));
            place.Rank = Weight(build, place.Country, place.Periods.LastOrDefault()?.Kind) * RankScale + Math.Min(place.Population ?? 0, RankScale - 1);
            ValidateZone(build, place);
        }

        foreach (var postal in build.PostalCodes.Values)
        {
            postal.Localities = [.. postal.Localities.OrderBy(l => l.Key, StringComparer.Ordinal)];
            foreach (var locality in postal.Localities)
            {
                locality.Municipalities = [.. locality.Municipalities.OrderBy(m => DateKeys.From(m.From)).ThenBy(m => m.Id, StringComparer.Ordinal)];
                foreach (var link in locality.Municipalities)
                {
                    link.FromKey = DateKeys.From(link.From);
                    link.ToKey = DateKeys.To(link.To);
                }
                locality.Search = RefSearchKeys.For(locality.Names.Entries.Select(e => e.Value));
                // The city-line unit ranks highest (§4.10): a postal locality outranks every place.
                locality.Rank = (Weight(build, postal.Country, "postal-locality") + (locality.Main ? 1 : 0)) * RankScale;
            }
            postal.Source = [.. postal.Source.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)];
        }

        foreach (var street in build.Streets.Values)
        {
            street.Periods = [.. street.Periods.OrderBy(p => DateKeys.From(p.From)).ThenBy(p => p.Municipality, StringComparer.Ordinal)];
            foreach (var period in street.Periods)
            {
                period.PostalCodes = [.. period.PostalCodes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal)];
                period.FromKey = DateKeys.From(period.From);
                period.ToKey = DateKeys.To(period.To);
            }
            foreach (var code in street.Codes)
            {
                code.FromKey = DateKeys.From(code.From);
                code.ToKey = DateKeys.To(code.To);
            }
            street.Codes = [.. street.Codes.OrderBy(c => c.Scheme, StringComparer.Ordinal).ThenBy(c => c.Value, StringComparer.Ordinal)];
            street.Search = RefSearchKeys.For(street.Names.Entries.Select(e => e.Value));
            street.Source = [.. street.Source.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)];
        }

        foreach (var lineage in build.Lineage.Values)
        {
            lineage.Operations = [.. lineage.Operations.OrderBy(o => o.Place, StringComparer.Ordinal).ThenBy(o => o.Op)];
            lineage.Sources = [.. lineage.Sources.OrderBy(s => s.Source, StringComparer.Ordinal)];
        }
        foreach (var zone in build.TimeZones.Values)
            zone.Countries = [.. zone.Countries.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal)];

        foreach (var document in build.Documents())
            SetHash(document);
    }

    /// <summary>Stamps <c>Hash</c> (the canonical hash without <c>Hash</c> and <c>Release</c>).</summary>
    public static void SetHash(object document)
        => document.GetType().GetProperty("Hash")!.SetValue(document, ReleaseCanonical.Hash(document));

    /// <summary>
    /// <see cref="RefPlace.Supersedes"/>/<see cref="RefPlace.SupersededBy"/> from the events' operations: an
    /// <see cref="LineageOp.End"/> into another place is a <see cref="SuccessionShare.Whole"/> edge (or
    /// <see cref="SuccessionShare.Part"/> for a split); a <see cref="LineageOp.Cede"/> is always a part.
    /// A place ended by an unresolved event is flagged (Q-R4).
    /// </summary>
    private static void DeriveEdges(RefDataBuild build)
    {
        foreach (var place in build.Places.Values)
        {
            place.Supersedes.Clear();
            place.SupersededBy.Clear();
            place.LineageUnresolved = false;
        }
        foreach (var lineage in build.Lineage.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            foreach (var op in lineage.Operations)
            {
                // Street merges and splits (Flemish diff files) are lineage too; places' edges come from place operations only.
                if (!RefIds.IsValid(op.Place, RefIds.Places))
                    continue;
                if (!build.Places.TryGetValue(op.Place, out var place))
                {
                    build.Warnings.Add($"{lineage.Id} names {op.Place}, which is not in the build.");
                    continue;
                }
                if (op.Op == LineageOp.End && lineage.Unresolved)
                    place.LineageUnresolved = true;
                if (op.Op is not (LineageOp.End or LineageOp.Cede) || op.Into is null)
                    continue;
                var share = op.Op == LineageOp.Cede ? SuccessionShare.Part : op.Share;
                place.SupersededBy.Add(new SuccessionEdge { Id = op.Into, Date = lineage.Date, Event = lineage.Id!, Share = share });
                if (build.Places.TryGetValue(op.Into, out var into))
                    into.Supersedes.Add(new SuccessionEdge { Id = op.Place, Date = lineage.Date, Event = lineage.Id!, Share = share });
            }
        }
    }

    private static List<SuccessionEdge> SortEdges(List<SuccessionEdge> edges)
        => [.. edges.OrderBy(e => e.Date).ThenBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Event, StringComparer.Ordinal)];

    /// <summary>
    /// A place's periods cut at every change of its ancestors: each period is intersected with its parent's segments,
    /// which carry the parent's own chain. A stretch where the parent did not exist keeps the parent alone.
    /// Hierarchy comes from periods, never from code digits (Zwijndrecht's sections).
    /// </summary>
    private static List<PlaceSegment> SegmentsOf(RefDataBuild build, RefPlace place, Dictionary<string, List<PlaceSegment>> memo, HashSet<string> stack)
    {
        if (memo.TryGetValue(place.Id!, out var known))
            return known;
        if (!stack.Add(place.Id!))
        {
            build.Warnings.Add($"{place.Id} is its own ancestor; its chain is cut.");
            return [];
        }

        var result = new List<PlaceSegment>();
        foreach (var period in place.Periods.OrderBy(p => DateKeys.From(p.From)))
        {
            var from = DateKeys.From(period.From);
            var to = DateKeys.To(period.To);
            if (period.Parent is null || !build.Places.TryGetValue(period.Parent, out var parent))
            {
                if (period.Parent is not null)
                    build.Warnings.Add($"{place.Id} names parent {period.Parent}, which is not in the build.");
                result.Add(Segment(from, to, period, period.Parent is null ? [] : [period.Parent]));
                continue;
            }

            var cursor = from;
            foreach (var parentSegment in SegmentsOf(build, parent, memo, stack).Where(s => s.FromKey < to && s.ToKey > from).OrderBy(s => s.FromKey))
            {
                var segmentFrom = Math.Max(from, parentSegment.FromKey);
                var segmentTo = Math.Min(to, parentSegment.ToKey);
                if (segmentFrom > cursor)
                    result.Add(Segment(cursor, segmentFrom, period, [period.Parent]));
                result.Add(Segment(segmentFrom, segmentTo, period, [period.Parent, .. parentSegment.Ancestors]));
                cursor = segmentTo;
            }
            if (cursor < to)
                result.Add(Segment(cursor, to, period, [period.Parent]));
        }

        stack.Remove(place.Id!);
        return memo[place.Id!] = Merge(result);
    }

    private static PlaceSegment Segment(int from, int to, PlacePeriod period, List<string> ancestors) => new()
    {
        FromKey = from,
        ToKey = to,
        Kind = period.Kind,
        Role = period.Role,
        Parent = period.Parent,
        Ancestors = ancestors,
    };

    /// <summary>Adjacent segments that say the same thing are one.</summary>
    private static List<PlaceSegment> Merge(List<PlaceSegment> segments)
    {
        var merged = new List<PlaceSegment>();
        foreach (var segment in segments.Where(s => s.FromKey < s.ToKey).OrderBy(s => s.FromKey))
        {
            if (merged.Count > 0 && merged[^1] is var last && last.ToKey == segment.FromKey && last.Kind == segment.Kind
                && last.Role == segment.Role && last.Parent == segment.Parent && last.Ancestors.SequenceEqual(segment.Ancestors))
                last.ToKey = segment.ToKey;
            else
                merged.Add(segment);
        }
        return merged;
    }

    private static long Weight(RefDataBuild build, string country, string? kind)
    {
        if (kind is null) return 1;
        if (build.Countries.TryGetValue(RefIds.CountryId(country), out var c)
            && c.Profile.Levels.FirstOrDefault(l => string.Equals(l.Key, kind, StringComparison.Ordinal)) is { } level)
            return level.RankWeight;
        return kind == "postal-locality" ? 9 : 1;
    }

    private static void ValidateZone(RefDataBuild build, RefPlace place)
    {
        if (place.TimeZone is { } zone && build.KnownTimeZones.Count > 0 && !build.KnownTimeZones.Contains(zone))
        {
            build.Warnings.Add($"{place.Id}: '{zone}' is not a tz database zone; dropped.");
            place.TimeZone = null;
            place.TimeZoneNote = $"'{zone}' is not in the tz database.";
        }
    }
}
