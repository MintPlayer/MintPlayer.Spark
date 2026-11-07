using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Startup checks on Reference attributes that the model hash cannot catch (reference add-new PRD D13, D17).
/// </summary>
public static class SparkReferences
{
    /// <summary>
    /// Every Reference that cannot work, as one message per offender:
    /// <list type="bullet">
    /// <item>its lookup <c>query</c> lists a different type than its <c>referenceType</c>, when both resolve
    /// (D13): the picker would offer rows of one type and store them as references to another, and
    /// <b>Add new</b> would create the type the query does not list;</item>
    /// <item>its <c>defaultAttribute</c> is not a String attribute of the referenced type (D17), so
    /// <b>Add new</b> would silently prefill nothing, or it is read-only, so the prefill would be dropped;</item>
    /// <item>with <paramref name="resolveClr"/> (startup, where the assemblies are loaded): it can add a
    /// value object whose <c>[ValueKey]</c> is not a writable model attribute and which derives no
    /// natural id (<see cref="IHasNaturalId"/>) either (D18).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Validate(
        IEnumerable<EntityTypeDefinition> entityTypes, IEnumerable<SparkQuery> queries, Func<string?, Type?>? resolveClr = null)
    {
        var types = entityTypes.ToList();
        var queryList = queries.ToList();
        var problems = new List<string>();

        foreach (var owner in types)
        {
            foreach (var attribute in owner.Attributes)
            {
                if (attribute.DataType != "Reference")
                    continue;

                var where = $"Reference '{owner.Name}.{attribute.Name}'";
                var byClr = attribute.ReferenceType is { Length: > 0 } clrName
                    ? types.FirstOrDefault(t => string.Equals(t.ClrType, clrName, StringComparison.Ordinal))
                    : null;
                var query = attribute.Query is { Length: > 0 } lookup
                    ? queryList.FirstOrDefault(q => string.Equals(q.Name, lookup, StringComparison.Ordinal))
                        ?? queryList.FirstOrDefault(q => Names(lookup, q))
                    : null;
                var byQuery = query?.EntityType is { Length: > 0 } rowType
                    ? types.FirstOrDefault(t => string.Equals(t.Name, rowType, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (byClr is not null && byQuery is not null && byClr.Id != byQuery.Id)
                    problems.Add(
                        $"{where} references '{byClr.Name}' ({attribute.ReferenceType}), but its lookup query '{query!.Name}' lists '{byQuery.Name}'.");

                if (attribute.DefaultAttribute is not { Length: > 0 } defaultAttribute)
                    continue;

                var target = byClr ?? byQuery;
                var prefilled = target?.Attributes.FirstOrDefault(a => string.Equals(a.Name, defaultAttribute, StringComparison.Ordinal));
                if (attribute.CanAddNewReference != true)
                    problems.Add($"{where} names defaultAttribute '{defaultAttribute}' without canAddNewReference.");
                else if (target is null)
                    problems.Add($"{where} names defaultAttribute '{defaultAttribute}', but its referenced type does not resolve.");
                else if (prefilled is null)
                    problems.Add($"{where} names defaultAttribute '{defaultAttribute}', which is not an attribute of '{target.Name}'.");
                else if (!string.Equals(prefilled.DataType, "string", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{where} names defaultAttribute '{defaultAttribute}', which is a {prefilled.DataType} attribute of '{target.Name}', not a String.");
                else if (prefilled.IsReadOnly)
                    problems.Add($"{where} names defaultAttribute '{defaultAttribute}', which is read-only on '{target.Name}', so Add new would drop the prefilled value.");
            }
        }

        // D18: Add new on a value object confirms it with its [ValueKey] as id, read from the posted
        // values. A key that is not a writable model attribute would leave every new row keyless.
        if (resolveClr is not null)
        {
            foreach (var owner in types)
            {
                foreach (var attribute in owner.Attributes.Where(a => a.DataType == "Reference" && a.CanAddNewReference == true))
                {
                    var target = attribute.ReferenceType is { Length: > 0 } clrName
                        ? types.FirstOrDefault(t => string.Equals(t.ClrType, clrName, StringComparison.Ordinal))
                        : null;
                    // A natural id (IHasNaturalId) derives the key from the entity's own values, so the key
                    // need not be an attribute (owner, 2026-11-07, revising D18).
                    if (target is null || resolveClr(target.ClrType) is not { } clr
                        || typeof(IHasNaturalId).IsAssignableFrom(clr)
                        || Abstractions.Model.SparkValueObjects.GetKeyPropertyName(clr) is not { } keyName)
                        continue;

                    var key = target.Attributes.FirstOrDefault(a => string.Equals(a.Name, keyName, StringComparison.Ordinal));
                    if (key is null || key.IsReadOnly)
                        problems.Add(
                            $"Reference '{owner.Name}.{attribute.Name}' has canAddNewReference, but the value object '{target.Name}' has no writable model attribute for its [ValueKey] '{keyName}', so a new row would have no key.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// After the name (what a Reference's query holds, as <c>GetReferenceTarget</c> resolves it): whether
    /// <paramref name="key"/> names <paramref name="query"/> as <c>IQueryLoader.ResolveQuery</c> does, a Guid
    /// as an id and anything else as an alias (declared or derived), case-insensitively.
    /// </summary>
    private static bool Names(string key, SparkQuery query)
        => Guid.TryParse(key, out var id)
            ? id == query.Id
            : string.Equals(key, query.Alias ?? SparkQueryAliases.Derive(query.Name), StringComparison.OrdinalIgnoreCase);
}
