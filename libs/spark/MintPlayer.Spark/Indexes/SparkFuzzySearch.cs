using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark;

/// <summary>
/// Makes Spark's search typo-tolerant on an index's searchable fields (commerce X18, "fuzzy by default"
/// 2026-10-19): next to the substring wildcard, each word also matches by edit distance, so <c>volkswagon</c> finds
/// <c>Volkswagen</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an RQL rewrite.</b> RavenDB's <c>Fuzzy</c> exists only on the document-query API, must follow a
/// <c>WhereEquals</c> immediately, and the LINQ-to-document-query bridge breaks once row security adds its
/// <c>Where</c> (measured on 7.2.5). Spark's search is composed in LINQ, so the fuzzy arm is added where LINQ hands
/// over the finished query: <see cref="IDocumentQueryCustomization.BeforeQueryExecuted"/>. The rewrite touches only
/// the <c>search(Field, $p)</c> clauses whose parameter is Spark's own search term, on fields the caller named.
/// </para>
/// <para>
/// <b>Never splices user text.</b> Every word travels as a query parameter (<c>$sparkWildcardN</c>,
/// <c>$sparkFuzzyN</c>). What is written into the RQL is the field name — taken from RavenDB's own rendering and
/// validated with <see cref="RqlIdentifier.FieldPath"/> — the parameter names, and a similarity Spark computes.
/// </para>
/// <para>
/// <b>Shape.</b> <c>search(F, $p, and)</c> over the words <c>*a* *b*</c> becomes
/// <c>((boost(search(F, $w0), 2) or fuzzy(F = $f0, s0)) and (boost(search(F, $w1), 2) or fuzzy(F = $f1, s1)))</c>:
/// every word must still match the field, now either as a substring or within its edit budget. Wildcards and fuzzy
/// cannot share a term (a <c>*</c> counts as edits, measured), so they are separate arms. The substring arm is
/// boosted so an exact or substring hit outranks a typo match when the query orders by score. A word shorter than
/// <see cref="MinimumWordLength"/> keeps only the substring arm: one edit on three letters matches noise.
/// </para>
/// <para>
/// <b>Only on Lucene.</b> Corax refuses <c>fuzzy</c> and fails the whole query, so the caller adds the rewrite only
/// for an index the server confirmed running Lucene (<see cref="SparkSearchEngine.RunsLuceneAsync"/>).
/// </para>
/// </remarks>
internal static partial class SparkFuzzySearch
{
    /// <summary>Words shorter than this are matched by substring only.</summary>
    internal const int MinimumWordLength = 4;

    /// <summary>The boost on the substring arm, so a typo match ranks below an exact one.</summary>
    internal const int SubstringBoost = 2;

    internal const string WildcardParameterPrefix = "sparkWildcard";
    internal const string FuzzyParameterPrefix = "sparkFuzzy";

    /// <summary>
    /// The minimum similarity for a word of <paramref name="length"/> characters, or <see langword="null"/> when the
    /// word is too short to match fuzzily.
    /// </summary>
    /// <remarks>
    /// Lucene's similarity is <c>1 − edits / min(len(word), len(term))</c> and must be strictly greater than the
    /// threshold, so a fixed threshold means a different number of edits per word length. This allows one edit up to
    /// seven characters and two from eight, and divides by the length minus the edits so a deletion (the shorter of
    /// the two decides) still fits. Rounded down to two decimals, always inside RavenDB's valid range <c>[0, 1)</c>.
    /// </remarks>
    internal static decimal? Similarity(int length)
    {
        if (length < MinimumWordLength) return null;

        var edits = length >= 8 ? 2 : 1;
        var similarity = 1m - (decimal)edits / (length - edits) - 0.01m;
        return Math.Floor(similarity * 100m) / 100m;
    }

    /// <summary>
    /// Adds the fuzzy arm to <paramref name="queryable"/>'s search on <paramref name="fuzzyFields"/>, for the search
    /// <paramref name="term"/> Spark built (<c>*word*</c> per word). Returns the queryable unchanged when it is not a
    /// RavenDB query or no word is long enough.
    /// </summary>
    internal static object Apply(object queryable, string term, IReadOnlySet<string> fuzzyFields)
    {
        if (fuzzyFields.Count == 0 || !Words(term).Any(w => Similarity(w.Length) is not null)) return queryable;

        var ravenQueryable = queryable.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRavenQueryable<>));
        if (ravenQueryable is null) return queryable;

        Action<IDocumentQueryCustomization> customize = c => c.BeforeQueryExecuted(q => Rewrite(q, term, fuzzyFields));
        return ravenQueryable.GetMethod(nameof(IRavenQueryable<object>.Customize))!.Invoke(queryable, [customize])!;
    }

    /// <summary>
    /// Rewrites every <c>search(Field, $p)</c> clause of <paramref name="query"/> whose field is in
    /// <paramref name="fuzzyFields"/> and whose parameter holds <paramref name="term"/>. A query with no such clause
    /// is left as it is, so a rendering this does not recognise degrades to substring search rather than failing.
    /// </summary>
    internal static void Rewrite(IndexQuery query, string term, IReadOnlySet<string> fuzzyFields)
    {
        if (query.Query is not { } rql || query.QueryParameters is not { } parameters) return;

        var words = Words(term);
        if (words.Count == 0) return;

        var rewritten = false;
        var result = SearchClause().Replace(rql, match =>
        {
            var field = match.Groups["field"].Value;
            if (!fuzzyFields.Contains(field)) return match.Value;
            if (!parameters.TryGetValue(match.Groups["param"].Value, out var value)
                || value is not string text || !string.Equals(text, term, StringComparison.Ordinal))
                return match.Value;

            rewritten = true;
            return Clause(RqlIdentifier.FieldPath(field), words);
        });
        if (!rewritten) return;

        for (var i = 0; i < words.Count; i++)
        {
            parameters[WildcardParameterPrefix + i] = $"*{words[i]}*";
            if (Similarity(words[i].Length) is not null)
                parameters[FuzzyParameterPrefix + i] = words[i].ToLowerInvariant();
        }
        query.Query = result;
    }

    /// <summary>One field's replacement: every word must match, by substring or within its edit budget.</summary>
    private static string Clause(string field, IReadOnlyList<string> words)
    {
        var clause = new StringBuilder("(");
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) clause.Append(" and ");

            var substring = $"boost(search({field}, ${WildcardParameterPrefix}{i}), {SubstringBoost})";
            if (Similarity(words[i].Length) is { } similarity)
                clause.Append($"({substring} or fuzzy({field} = ${FuzzyParameterPrefix}{i}, {similarity.ToString(CultureInfo.InvariantCulture)}))");
            else
                clause.Append(substring);
        }
        return clause.Append(')').ToString();
    }

    /// <summary>The words of a term built by <c>QueryExecutor.BuildSearchTerm</c>: <c>*a* *b*</c> → a, b.</summary>
    internal static IReadOnlyList<string> Words(string term)
        => [.. term.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('*'))
            .Where(w => w.Length > 0)];

    /// <summary>
    /// RavenDB's rendering of a LINQ <c>Search</c>: <c>search(Field, $p0)</c>, with <c>, and</c> for
    /// <c>SearchOperator.And</c>. The field must look like an identifier, so a quoted or escaped one is left alone.
    /// </summary>
    [GeneratedRegex(@"search\((?<field>[A-Za-z][A-Za-z0-9_]*), \$(?<param>[A-Za-z_][A-Za-z0-9_]*)(?:, and)?\)", RegexOptions.CultureInvariant)]
    private static partial Regex SearchClause();
}
