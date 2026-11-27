using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Queries;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// #536 O11 (D16 for nullable members): a field the document never wrote is <c>null</c>, as a <c>NULL</c> column is on
/// SQL. RavenDB's own <c>Field = null</c> matches only a field written as JSON null, and <c>Field != null</c> matches an
/// absent one too (<c>AbsentVersusNullFieldTests</c>), and its LINQ provider has no way to say "exists". So the RQL the
/// LINQ provider generated is rewritten before it is sent, in its <c>where</c> clause only:
/// <list type="bullet">
/// <item><c>F = $p</c> with <c>$p</c> null becomes <c>(F = $p or (true and not exists(F)))</c></item>
/// <item><c>F != $p</c> with <c>$p</c> null becomes <c>(F != $p and exists(F))</c></item>
/// </list>
/// The values stay parameters; the field name comes from RavenDB's own RQL and is checked with
/// <see cref="RqlIdentifier.FieldPath"/> before it is written twice. Only Spark's session applies it, so a raw RavenDB
/// session keeps RavenDB's meaning.
/// </summary>
public static partial class RavenAbsentNullRewriter
{
    private static readonly string[] ClauseEnds = [" order by ", " select ", " update ", " include ", " limit ", " filter "];

    /// <summary>Rewrites <paramref name="query"/>'s null comparisons in place.</summary>
    public static void Rewrite(IndexQuery query)
    {
        if (query.QueryParameters is not { Count: > 0 } parameters || query.Query is not { } rql)
            return;

        var where = rql.IndexOf(" where ", StringComparison.Ordinal);
        if (where < 0)
            return;
        var start = where + " where ".Length;
        var end = rql.Length;
        foreach (var keyword in ClauseEnds)
        {
            var at = rql.IndexOf(keyword, start, StringComparison.Ordinal);
            if (at >= 0 && at < end)
                end = at;
        }

        var clause = rql[start..end];
        var rewritten = Comparison().Replace(clause, match =>
        {
            var parameter = match.Groups["param"].Value;
            if (!parameters.TryGetValue(parameter, out var value) || value is not null)
                return match.Value;
            var field = RqlIdentifier.FieldPath(match.Groups["field"].Value);
            return match.Groups["op"].Value == "="
                ? $"({field} = ${parameter} or (true and not exists({field})))"
                : $"({field} != ${parameter} and exists({field}))";
        });

        if (!ReferenceEquals(rewritten, clause) && rewritten != clause)
            query.Query = string.Concat(rql.AsSpan(0, start), rewritten, rql.AsSpan(end));
    }

    // A plain field path (RavenDB's generated RQL quotes nothing it compares here), "=" or "!=", and a parameter.
    // id() and quoted names do not match, so they are left as they are.
    [GeneratedRegex(@"(?<![\w.'""$])(?<field>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?<op>!=|=)\s*\$(?<param>[A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex Comparison();
}
