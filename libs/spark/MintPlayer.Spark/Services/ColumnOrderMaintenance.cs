using MintPlayer.Spark.Abstractions;
using System.Reflection;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// What <c>--spark-synchronize-model</c> does to an existing <see cref="SparkQuery.ColumnOrder"/> (#284,
/// R5), computed once so that the synchronizer applies it and <c>--spark-verify-model</c> reports it as
/// drift without the two ever disagreeing.
/// </summary>
/// <remarks>
/// Two changes only, and nothing else about the list moves (its order, its <c>!</c> markers, entries for
/// existing attributes that are off the surface):
/// <list type="bullet">
/// <item><b>Append</b> each query-surface attribute the list mentions in neither form, at the end, by
/// (<c>order</c>, name) (D6). Visible, unless the query is bound to a non-default index whose projection
/// does not back it: then hidden, since drawing it would draw a null-filled column (D10, S2).</item>
/// <item><b>Remove</b> each entry no attribute has the name of (D5, a narrow exception to #253): the
/// list is a reference, like a dead <c>indexName</c>, not data.</item>
/// </list>
/// A null list is never created (D4), and a list a library owns is left to it (D16).
/// </remarks>
internal static class ColumnOrderMaintenance
{
    /// <summary>One change to a list: an appended surface attribute or a removed dead entry.</summary>
    /// <param name="Name">The attribute name (an appended one) or the entry's name (a removed one).</param>
    /// <param name="Appended">True for an append, false for a removal.</param>
    /// <param name="Hidden">For an append: appended as <c>!Name</c>.</param>
    /// <param name="IndexName">For a hidden append: the index whose projection does not back it.</param>
    internal sealed record Change(string Name, bool Appended, bool Hidden = false, string? IndexName = null)
    {
        /// <summary>The console note the synchronizer prints for this change on <paramref name="queryName"/>.</summary>
        public string Note(string queryName) => Appended
            ? Hidden
                ? $"Appended column '!{Name}' to columnOrder of query '{queryName}': index '{IndexName}' does not back it, so it is hidden."
                : $"Appended column '{Name}' to columnOrder of query '{queryName}'."
            : $"Removed column '{Name}' from columnOrder of query '{queryName}': no attribute named '{Name}' exists.";

        /// <summary>The same change as verify reports it: what a synchronize would do.</summary>
        public string Drift => Appended
            ? $"synchronize would append '{(Hidden ? "!" : "")}{Name}'"
            : $"synchronize would remove '{Name}': no attribute has that name";
    }

    /// <summary>
    /// The list <paramref name="query"/>'s <see cref="SparkQuery.ColumnOrder"/> should be over
    /// <paramref name="rowType"/>, and the changes that make it so; null when it has no list or needs none.
    /// </summary>
    public static (string[] ColumnOrder, IReadOnlyList<Change> Changes)? Maintain(
        EntityTypeDefinition rowType, SparkQuery query, IIndexCatalog? indexCatalog)
    {
        if (query.ColumnOrder is not { } list)
            return null;

        var changes = new List<Change>();
        var kept = new List<string>(list.Length);
        foreach (var raw in list)
        {
            var entry = ColumnOrderEntry.Parse(raw);
            if (FindAttribute(rowType, entry.Name) is null)
            {
                changes.Add(new Change(entry.Name, Appended: false));
                continue;
            }
            kept.Add(raw);
        }

        var mentioned = new HashSet<string>(
            kept.Select(raw => ColumnOrderEntry.Parse(raw).Name), StringComparer.OrdinalIgnoreCase);
        var backed = BackedByBoundIndex(rowType, query, indexCatalog);
        foreach (var attribute in rowType.Attributes
                     .Where(a => a.ShowedOn.HasFlag(EShowedOn.Query) && !mentioned.Contains(a.Name))
                     .OrderBy(a => a.Order)
                     .ThenBy(a => a.Name, StringComparer.Ordinal))
        {
            var hidden = backed is not null && !backed.Contains(attribute.Name);
            kept.Add(hidden ? ColumnOrderEntry.HiddenPrefix + attribute.Name : attribute.Name);
            changes.Add(new Change(attribute.Name, Appended: true, hidden, hidden ? query.IndexName : null));
        }

        return changes.Count == 0 ? null : ([.. kept], changes);
    }

    /// <summary>
    /// For a query bound to an index other than its row type's own (the file's <c>indexName</c>), the
    /// attribute names that index's rows carry: its <c>[FromIndex]</c> projection's properties, or a
    /// viewless index's emitted fields. Null when the query uses the default binding or the index cannot
    /// be described — then nothing is known to be unbacked.
    /// </summary>
    /// <remarks>
    /// The default binding needs nothing here: the synchronizer already narrows <c>showedOn</c> to the
    /// default projection, so the query surface is exactly what it backs (S2).
    /// </remarks>
    public static IReadOnlySet<string>? BackedByBoundIndex(EntityTypeDefinition rowType, SparkQuery query, IIndexCatalog? indexCatalog)
    {
        if (indexCatalog is null
            || string.IsNullOrWhiteSpace(query.IndexName)
            || string.Equals(query.IndexName, rowType.IndexName, StringComparison.OrdinalIgnoreCase)
            || indexCatalog.GetByIndexName(query.IndexName) is not { } entry)
            return null;

        if (entry.ProjectionType is { } projection)
            return projection.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ModelSynchronizer.ViewlessQuerySurface(entry) is { } emitted
            ? emitted.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
    }

    /// <summary>
    /// Each library-shipped query's <c>columnOrder</c>, by query name, for <see cref="IsLibraryOwned"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> LibraryColumnOrders(IEnumerable<SparkLibrary> libraries)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var type in SparkModelFiles.LibraryTypes(libraries))
        {
            EntityTypeFile? file;
            try
            {
                file = JsonSerializer.Deserialize<EntityTypeFile>(type.Json, options);
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var query in file?.Queries ?? [])
            {
                if (query.ColumnOrder is { } columnOrder)
                    result.TryAdd(query.Name, columnOrder);
            }
        }
        return result;
    }

    /// <summary>
    /// Whether the composed list is the library's own, unchanged by the application (D16, S4). Such a list
    /// is left alone: appending or removing would copy the whole library list into the application's
    /// delta and freeze it there, while the runtime append (D8) already fills the gap.
    /// </summary>
    public static bool IsLibraryOwned(SparkQuery query, IReadOnlyDictionary<string, string[]> libraryColumnOrders)
        => query.ColumnOrder is { } composed
        && libraryColumnOrders.TryGetValue(query.Name, out var library)
        && composed.SequenceEqual(library, StringComparer.Ordinal);

    /// <summary>The attribute of <paramref name="rowType"/> named <paramref name="name"/>, case-insensitively.</summary>
    public static EntityAttributeDefinition? FindAttribute(EntityTypeDefinition rowType, string name)
        => rowType.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The attribute names a query over <paramref name="rowType"/> draws as columns, from the model alone
    /// (no caller's rights): the query surface, minus the <c>!</c>-hidden entries of its list.
    /// </summary>
    public static IReadOnlySet<string> DrawnColumns(EntityTypeDefinition rowType, SparkQuery query)
    {
        var hidden = ColumnOrderEntry.ParseAll(query.ColumnOrder)
            .Where(e => e.Hidden)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A name listed both plain and hidden is drawn by the runtime when the plain form comes first; verify
        // refuses that list outright, so either reading is fine here.
        return rowType.Attributes
            .Where(a => a.ShowedOn.HasFlag(EShowedOn.Query) && !hidden.Contains(a.Name))
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
