using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Contracts;
using MintPlayer.Spark.ReferenceData.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.ReferenceData.Search;

/// <summary>A query the API refuses with a 400 (§4.11 caps).</summary>
public sealed class RefQueryException(string message) : Exception(message);

/// <summary>A validated suggestion request.</summary>
public sealed record SuggestQuery(string Q, string? Country, IReadOnlyList<string> Kinds, string? Within, DateOnly AsOf, string? Lang, int Limit);

public sealed record StreetQuery(string Q, string? Country, string? Municipality, string? PostalCode, DateOnly AsOf, string? Lang, int Limit);

/// <summary>
/// The two-step typeahead (PRD §4.10, RD8, RD10):
/// <list type="number">
/// <item><b>Prefix step</b>: three tiers as lazy queries in one round trip: exact compact key; compact-key prefix;
/// every query token a prefix of some name token. Digits-only input queries postal codes ("97" → 9700…9799).</item>
/// <item><b>Fuzzy step</b>, only when the prefix step returns fewer than <c>FuzzyBelow</c> results:
/// <c>WhereEquals(Tokens, t).Fuzzy(s)</c> per token, ANDed, with the similarity translated from an edit distance per
/// token length. This is "fuzzy by default" applied after the exact tiers, so exact and prefix matches outrank it.</item>
/// <item><b>Deterministic re-ranking</b> in C#: tier, then edit distance, then the precomputed rank, then id. Never
/// the engine's score, which depends on what each database holds.</item>
/// </list>
/// Every value is a query parameter; the token count only picks how many fixed clauses are added; <c>within</c> must
/// match the id grammar and <c>kinds</c> the country profile's levels (repo rule: never splice into RQL).
/// </summary>
public sealed class RefTypeahead(IOptions<SparkReferenceDataOptions> options)
{
    private ReferenceDataApiOptions Api => options.Value.Api;

    /// <summary>Folds and checks <paramref name="q"/>: 1..MaxQueryLength characters, at most MaxTokens tokens, no control characters, a last token of at least 2.</summary>
    public (IReadOnlyList<string> Tokens, string Compact) Fold(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            throw new RefQueryException("q is required.");
        if (q.Any(char.IsControl))
            throw new RefQueryException("q holds control characters.");
        var (tokens, compact) = RefSearchKeys.Query(q);
        if (compact.Length == 0 || compact.Length > Api.MaxQueryLength)
            throw new RefQueryException($"q must hold 1 to {Api.MaxQueryLength} letters or digits.");
        if (tokens.Count > Api.MaxTokens)
            throw new RefQueryException($"q may hold at most {Api.MaxTokens} words.");
        var last = tokens[^1];
        if (last.Length < 2 && !last.Any(c => c > '⹿'))
            throw new RefQueryException("The last word of q needs at least 2 characters.");
        return (tokens, compact);
    }

    public int Limit(int? limit) => Math.Clamp(limit ?? Api.DefaultLimit, 1, Api.MaxLimit);

    /// <summary>Places and postal localities, ranked (step 3), at most <c>query.Limit</c>.</summary>
    public async Task<IReadOnlyList<RankedEntry>> SuggestAsync(IAsyncDocumentSession session, SuggestQuery query, CancellationToken cancellationToken)
    {
        var (tokens, compact) = Fold(query.Q);
        var asOf = DateKeys.Of(query.AsOf);
        var take = query.Limit * 4;

        IAsyncDocumentQuery<TypeaheadEntry> Base()
        {
            var q = session.Advanced.AsyncDocumentQuery<TypeaheadEntry>(RefDataIndexes.Localities)
                .WhereLessThanOrEqual(nameof(TypeaheadEntry.FromKey), asOf)
                .AndAlso().WhereGreaterThan(nameof(TypeaheadEntry.ToKey), asOf);
            if (query.Country is { } country)
                q = q.AndAlso().WhereEquals(nameof(TypeaheadEntry.Country), country);
            if (query.Within is { } within)
                q = q.AndAlso().WhereEquals(nameof(TypeaheadEntry.Within), within);
            if (query.Kinds.Count > 0)
                q = q.AndAlso().WhereIn(nameof(TypeaheadEntry.Kind), query.Kinds);
            return q;
        }

        var tiers = new List<(int Tier, Lazy<Task<IEnumerable<TypeaheadEntry>>> Results)>();
        if (compact.All(char.IsAsciiDigit))
        {
            tiers.Add((1, Base().AndAlso().WhereStartsWith(nameof(TypeaheadEntry.PostalCode), compact)
                .SelectFields<TypeaheadEntry>().Take(take).LazilyAsync()));
        }
        else
        {
            tiers.Add((1, Base().AndAlso().WhereEquals(nameof(TypeaheadEntry.Compact), compact).SelectFields<TypeaheadEntry>().Take(take).LazilyAsync()));
            tiers.Add((2, Base().AndAlso().WhereStartsWith(nameof(TypeaheadEntry.Compact), compact).SelectFields<TypeaheadEntry>().Take(take).LazilyAsync()));
            var tokenQuery = Base();
            foreach (var token in tokens)
                tokenQuery = tokenQuery.AndAlso().WhereStartsWith(nameof(TypeaheadEntry.Tokens), token);
            tiers.Add((3, tokenQuery.SelectFields<TypeaheadEntry>().Take(take).LazilyAsync()));
        }
        await session.Advanced.Eagerly.ExecuteAllPendingLazyOperationsAsync(cancellationToken);

        var ranked = new Dictionary<string, RankedEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tier, results) in tiers)
            foreach (var entry in await results.Value)
                Keep(ranked, entry, tier, compact, fuzzy: false);

        if (ranked.Count < Api.FuzzyBelow && !compact.All(char.IsAsciiDigit))
        {
            var fuzzy = Base();
            foreach (var token in tokens)
                fuzzy = fuzzy.AndAlso().WhereEquals(nameof(TypeaheadEntry.Tokens), token).Fuzzy(Similarity(token));
            foreach (var entry in await fuzzy.SelectFields<TypeaheadEntry>().Take(take).ToListAsync(cancellationToken))
                Keep(ranked, entry, 4, compact, fuzzy: true);
        }

        return [.. ranked.Values
            .OrderBy(r => r.Tier).ThenBy(r => r.Distance).ThenByDescending(r => r.Entry.Rank)
            .ThenBy(r => r.Entry.DocumentId, StringComparer.Ordinal).ThenBy(r => r.Entry.LocalityKey, StringComparer.Ordinal)
            .Take(query.Limit)];
    }

    /// <summary>Streets inside a municipality or postal code: a street query is always narrowed, so it never scans millions.</summary>
    public async Task<IReadOnlyList<RankedStreet>> StreetsAsync(IAsyncDocumentSession session, StreetQuery query, CancellationToken cancellationToken)
    {
        if (query.Municipality is null && query.PostalCode is null)
            throw new RefQueryException("A street query needs municipality or postalCode.");
        var (tokens, compact) = Fold(query.Q);
        var asOf = DateKeys.Of(query.AsOf);
        var take = query.Limit * 4;

        IAsyncDocumentQuery<StreetTypeaheadEntry> Base()
        {
            var q = session.Advanced.AsyncDocumentQuery<StreetTypeaheadEntry>(RefDataIndexes.Streets)
                .WhereLessThanOrEqual(nameof(StreetTypeaheadEntry.FromKey), asOf)
                .AndAlso().WhereGreaterThan(nameof(StreetTypeaheadEntry.ToKey), asOf);
            if (query.Municipality is { } municipality)
                q = q.AndAlso().WhereEquals(nameof(StreetTypeaheadEntry.Municipality), municipality);
            if (query.PostalCode is { } postalCode)
                q = q.AndAlso().WhereEquals(nameof(StreetTypeaheadEntry.PostalCodes), postalCode);
            return q;
        }

        var exact = Base().AndAlso().WhereEquals(nameof(StreetTypeaheadEntry.Compact), compact).SelectFields<StreetTypeaheadEntry>().Take(take).LazilyAsync();
        var prefix = Base().AndAlso().WhereStartsWith(nameof(StreetTypeaheadEntry.Compact), compact).SelectFields<StreetTypeaheadEntry>().Take(take).LazilyAsync();
        var words = Base();
        foreach (var token in tokens)
            words = words.AndAlso().WhereStartsWith(nameof(StreetTypeaheadEntry.Tokens), token);
        var tokenTier = words.SelectFields<StreetTypeaheadEntry>().Take(take).LazilyAsync();
        await session.Advanced.Eagerly.ExecuteAllPendingLazyOperationsAsync(cancellationToken);

        var ranked = new Dictionary<string, RankedStreet>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tier, results) in new[] { (1, exact), (2, prefix), (3, tokenTier) })
            foreach (var entry in await results.Value)
                KeepStreet(ranked, entry, tier, compact, false);

        if (ranked.Count < Api.FuzzyBelow)
        {
            var fuzzy = Base();
            foreach (var token in tokens)
                fuzzy = fuzzy.AndAlso().WhereEquals(nameof(StreetTypeaheadEntry.Tokens), token).Fuzzy(Similarity(token));
            foreach (var entry in await fuzzy.SelectFields<StreetTypeaheadEntry>().Take(take).ToListAsync(cancellationToken))
                KeepStreet(ranked, entry, 4, compact, true);
        }

        return [.. ranked.Values.OrderBy(r => r.Tier).ThenBy(r => r.Distance).ThenBy(r => r.Entry.DocumentId, StringComparer.Ordinal).Take(query.Limit)];
    }

    /// <summary>The fuzzy similarity of a token: <c>1 − d/len</c> for the edit distance allowed at that length (1 up to 7 characters, else 2).</summary>
    public static decimal Similarity(string token)
    {
        var distance = token.Length <= 2 ? 0 : token.Length <= 7 ? 1 : 2;
        return Math.Round(1m - (decimal)distance / Math.Max(1, token.Length), 2);
    }

    private static void Keep(Dictionary<string, RankedEntry> ranked, TypeaheadEntry entry, int tier, string compact, bool fuzzy)
    {
        var key = $"{entry.Type}|{entry.DocumentId}|{entry.LocalityKey}";
        var distance = entry.Compact.Count == 0 ? compact.Length : entry.Compact.Min(c => Distance(compact, c.Length > compact.Length && tier < 4 ? c[..compact.Length] : c));
        if (!ranked.TryGetValue(key, out var existing) || (tier, distance).CompareTo((existing.Tier, existing.Distance)) < 0)
            ranked[key] = new RankedEntry(entry, tier, distance, fuzzy);
    }

    private static void KeepStreet(Dictionary<string, RankedStreet> ranked, StreetTypeaheadEntry entry, int tier, string compact, bool fuzzy)
    {
        var distance = entry.Compact.Count == 0 ? compact.Length : entry.Compact.Min(c => Distance(compact, c.Length > compact.Length && tier < 4 ? c[..compact.Length] : c));
        if (!ranked.TryGetValue(entry.DocumentId, out var existing) || (tier, distance).CompareTo((existing.Tier, existing.Distance)) < 0)
            ranked[entry.DocumentId] = new RankedStreet(entry, tier, distance, fuzzy);
    }

    /// <summary>Levenshtein distance (ordinal).</summary>
    public static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}

public sealed record RankedEntry(TypeaheadEntry Entry, int Tier, int Distance, bool Fuzzy);

public sealed record RankedStreet(StreetTypeaheadEntry Entry, int Tier, int Distance, bool Fuzzy);
