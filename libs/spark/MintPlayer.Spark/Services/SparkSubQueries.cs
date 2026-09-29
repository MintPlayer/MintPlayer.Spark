using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Matching a parent type's <see cref="EntityTypeDefinition.Queries"/> entries to queries, and
/// validating what an entry may state (#460, D17, D19).
/// </summary>
public static class SparkSubQueries
{
    /// <summary>
    /// Whether <paramref name="entry"/> names <paramref name="query"/>: by alias (declared or derived
    /// from the name), by name, or by id — case-insensitively, as the query loader resolves them.
    /// </summary>
    public static bool Names(SparkSubQuery entry, SparkQuery query)
    {
        var key = entry.Query;
        if (string.IsNullOrEmpty(key)) return false;

        return string.Equals(key, query.Alias ?? SparkQueryAliases.Derive(query.Name), StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, query.Name, StringComparison.OrdinalIgnoreCase)
            || (Guid.TryParse(key, out var id) && id == query.Id);
    }

    /// <summary>The entry of <paramref name="parentType"/> that names <paramref name="query"/>, if any.</summary>
    public static SparkSubQuery? FindEntry(EntityTypeDefinition parentType, SparkQuery query)
        => parentType.Queries.FirstOrDefault(entry => Names(entry, query));

    /// <summary>
    /// Every <c>parentReference</c> that cannot work, as one message per offender: the named attribute
    /// must exist on the query's row type, be a single <c>Reference</c>, and reference the parent's CLR
    /// type. Checked for the query's own <see cref="SparkQuery.ParentReference"/> against every parent
    /// that declares it, and for each entry's override.
    /// </summary>
    public static IReadOnlyList<string> ValidateParentReferences(
        IEnumerable<EntityTypeDefinition> entityTypes, IEnumerable<SparkQuery> queries)
    {
        var types = entityTypes.ToList();
        var queryList = queries.ToList();
        var problems = new List<string>();

        foreach (var parentType in types)
        {
            foreach (var entry in parentType.Queries)
            {
                var query = queryList.FirstOrDefault(q => Names(entry, q));
                if (query is null)
                    continue; // An unresolvable alias is SubQueryPruner's warning, not this one.

                var name = entry.ParentReference ?? query.ParentReference;
                if (string.IsNullOrEmpty(name))
                    continue;

                var rowType = types.FirstOrDefault(t => string.Equals(t.Name, query.EntityType, StringComparison.OrdinalIgnoreCase));
                var attribute = rowType?.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
                var where = entry.ParentReference is not null
                    ? $"'{parentType.Name}.queries' entry '{entry.Query}'"
                    : $"query '{query.Name}' (a sub-query of '{parentType.Name}')";

                if (rowType is null)
                    problems.Add($"{where} names parentReference '{name}', but the query's entityType '{query.EntityType}' is not a known type.");
                else if (attribute is null)
                    problems.Add($"{where} names parentReference '{name}', which is not an attribute of '{rowType.Name}'.");
                else if (attribute.DataType != "Reference" || attribute.IsArray)
                    problems.Add($"{where} names parentReference '{name}', which is not a single Reference attribute of '{rowType.Name}'.");
                else if (!string.Equals(attribute.ReferenceType, parentType.ClrType, StringComparison.Ordinal))
                    problems.Add($"{where} names parentReference '{name}', which references '{attribute.ReferenceType}', not the parent's type '{parentType.ClrType}'.");
            }
        }

        return problems;
    }
}
