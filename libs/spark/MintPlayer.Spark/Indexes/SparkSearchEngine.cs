using System.Reflection;
using MintPlayer.Spark.Abstractions;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark;

/// <summary>
/// Chooses the search engine of each index from its declarations (commerce X18): an index with a
/// <see cref="FuzzyAttribute"/>-marked field runs on Lucene, every other index keeps the server default.
/// </summary>
/// <remarks>
/// <para>
/// Corax, the default on Community and Developer servers, refuses <c>Fuzzy(...)</c> at query time
/// (<c>InvalidQueryException: Method 'Fuzzy' is not supported</c>, measured on 7.2.5), and the engine is
/// selectable per index on every licence.
/// </para>
/// <para>
/// <b>Deterministic.</b> The engine is derived from code only, never from the server's or the licence's default,
/// so the same build always deploys the same definition. A generated index (<c>[GenerateIndex]</c> or a
/// library-declared <c>[SparkIndexMap]</c> index) sets <c>SearchEngineType</c> in its own constructor; a
/// hand-written index gets it here, from a <c>[FromIndex]</c> projection or the mapped type that marks a field
/// fuzzy (<see cref="RequiresLucene"/>). Adding or
/// removing the marker changes the definition's configuration, which RavenDB redeploys side by side.
/// <see cref="Reconcile"/> then checks the engine each fuzzy index actually runs, and resets one that still runs
/// another engine side by side: RavenDB applies an engine only to a new or reset index, so the definition alone
/// is not proof.
/// </para>
/// </remarks>
public static class SparkSearchEngine
{
    /// <summary>
    /// The index types that need Lucene because a <c>[FromIndex]</c> projection of theirs marks a property
    /// <see cref="FuzzyAttribute"/>.
    /// </summary>
    /// <param name="projectionTypes">Candidate types; those without <c>[FromIndex]</c> are ignored.</param>
    public static IReadOnlySet<Type> FuzzyIndexTypes(IEnumerable<Type> projectionTypes)
    {
        var result = new HashSet<Type>();

        foreach (var type in projectionTypes)
        {
            if (type.GetCustomAttribute<FromIndexAttribute>() is not { IndexType: { } indexType }) continue;
            if (HasFuzzyProperty(type)) result.Add(indexType);
        }

        return result;
    }

    /// <summary>Whether <paramref name="type"/> declares or inherits a public property marked <see cref="FuzzyAttribute"/>.</summary>
    public static bool HasFuzzyProperty(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.IsDefined(typeof(FuzzyAttribute), inherit: true));

    /// <summary>
    /// Whether <paramref name="indexType"/> needs Lucene: it is in <paramref name="fuzzyIndexTypes"/> (a projection
    /// marks a field), or a type argument of its base marks one — the mapped document of a single-map index, the
    /// result of a multi-map. Analyzer SPARK063 applies the same rule.
    /// </summary>
    public static bool RequiresLucene(Type indexType, IReadOnlySet<Type> fuzzyIndexTypes)
    {
        if (fuzzyIndexTypes.Contains(indexType)) return true;

        for (var current = indexType.BaseType; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            if (current.GetGenericArguments().Any(argument => argument.IsClass && HasFuzzyProperty(argument)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Sets <see cref="SearchEngineType.Lucene"/> on every task that <see cref="RequiresLucene"/> and does not choose
    /// an engine itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A fuzzy index pins another engine in its constructor. Every fuzzy query on it would throw, so this is a
    /// declaration error that stops startup rather than a choice to override silently.
    /// </exception>
    public static void Apply(IEnumerable<IAbstractIndexCreationTask> tasks, IReadOnlySet<Type> fuzzyIndexTypes)
    {
        foreach (var task in tasks)
        {
            if (!RequiresLucene(task.GetType(), fuzzyIndexTypes)) continue;

            switch (task.SearchEngineType)
            {
                case SearchEngineType.Lucene:
                    continue;

                case null or SearchEngineType.None:
                    if (task is not AbstractIndexCreationTaskBase<IndexDefinition> settable)
                        throw new InvalidOperationException(
                            $"Index '{task.IndexName}' ({task.GetType().FullName}) has a [Fuzzy] field but is not an index " +
                            "task whose search engine Spark can set. Set SearchEngineType = SearchEngineType.Lucene in its constructor.");
                    settable.SearchEngineType = SearchEngineType.Lucene;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Index '{task.IndexName}' ({task.GetType().FullName}) pins SearchEngineType.{task.SearchEngineType} " +
                        "but a field of its projection or mapped type is marked [Fuzzy]. That engine refuses fuzzy queries; remove the pin " +
                        "so Spark deploys it on Lucene, or remove the [Fuzzy] marker.");
            }
        }
    }

    /// <summary>
    /// After deployment: resets, side by side, every Lucene-declared index in <paramref name="tasks"/> that still
    /// runs another engine and has no replacement under way. Returns the names of the indexes it reset.
    /// </summary>
    /// <remarks>
    /// A replacement already being built (<c>ReplacementOf/{name}</c>) is left alone: it was created from the new
    /// definition, so it already carries the declared engine, and resetting the live index again would only
    /// restart that work. While the replacement builds, the live index keeps serving queries on its old engine.
    /// </remarks>
    public static IReadOnlyList<string> Reconcile(IDocumentStore store, IEnumerable<IAbstractIndexCreationTask> tasks)
    {
        var declared = tasks
            .Where(t => t.SearchEngineType == SearchEngineType.Lucene)
            .Select(t => t.IndexName)
            .ToList();
        if (declared.Count == 0) return [];

        var running = store.Maintenance.Send(new GetIndexesStatisticsOperation())
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var reset = new List<string>();
        foreach (var name in declared)
        {
            if (!running.TryGetValue(name, out var stats)) continue;
            if (stats.SearchEngineType == SearchEngineType.Lucene) continue;
            if (running.ContainsKey(Constants.Documents.Indexing.SideBySideIndexNamePrefix + name)) continue;

            store.Maintenance.Send(new ResetIndexOperation(name, IndexResetMode.SideBySide));
            reset.Add(name);
        }

        return reset;
    }
}
