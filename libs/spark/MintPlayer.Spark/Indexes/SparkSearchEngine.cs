using System.Collections.Concurrent;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark;

/// <summary>
/// Chooses the search engine of each index from its declarations (commerce X18, "fuzzy by default" 2026-10-19):
/// an index with a <b>searchable</b> field (<c>FieldIndexing.Search</c>) or an explicit <see cref="FuzzyAttribute"/>
/// opt-in runs on Lucene, every other index keeps the server default.
/// </summary>
/// <remarks>
/// <para>
/// Spark's search is typo-tolerant on searchable fields (<c>fuzzy(...)</c> beside the substring wildcard, see
/// <see cref="SparkFuzzySearch"/>). Corax, the default on Community and Developer servers, refuses <c>Fuzzy(...)</c>
/// at query time (<c>InvalidQueryException: Method 'Fuzzy' is not supported</c>, measured on 7.2.5), and the engine
/// is selectable per index on every licence.
/// </para>
/// <para>
/// <b>Deterministic.</b> The engine is derived from code only, never from the server's or the licence's default,
/// so the same build always deploys the same definition. A generated index (<c>[GenerateIndex]</c> or a
/// library-declared <c>[SparkIndexMap]</c> index) sets <c>SearchEngineType</c> in its own constructor; a
/// hand-written index gets it here (<see cref="Apply"/>), from its own <c>Index(..., FieldIndexing.Search)</c>
/// declarations or a <c>[FromIndex]</c> projection or mapped type that opts a field in with <c>[Fuzzy]</c>.
/// Changing the engine changes the definition's configuration, which RavenDB redeploys side by side: every
/// existing search index of an app rebuilds once, on the first deploy of this version. <see cref="Reconcile"/>
/// then checks the engine each Lucene-declared index actually runs, and resets one that still runs another engine
/// side by side: RavenDB applies an engine only to a new or reset index, so the definition alone is not proof.
/// </para>
/// <para>
/// While a replacement builds, the live index keeps serving on its old engine. The query side therefore never
/// trusts the declaration: <see cref="RunsLuceneAsync"/> asks the server, and Spark's search adds fuzzy clauses only
/// to an index confirmed running Lucene.
/// </para>
/// </remarks>
public static class SparkSearchEngine
{
    /// <summary>
    /// The index types that need Lucene because a <c>[FromIndex]</c> projection of theirs opts a property in with
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

    /// <summary>
    /// Whether <paramref name="type"/> declares or inherits a public property that opts in with
    /// <see cref="FuzzyAttribute"/>. The opt-out <c>[Fuzzy(false)]</c> does not count.
    /// </summary>
    public static bool HasFuzzyProperty(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.GetCustomAttribute<FuzzyAttribute>(inherit: true) is { Enabled: true });

    /// <summary>
    /// Whether <paramref name="indexType"/> needs Lucene through an explicit <c>[Fuzzy]</c> opt-in: it is in
    /// <paramref name="fuzzyIndexTypes"/> (a projection marks a field), or a type argument of its base marks one — the
    /// mapped document of a single-map index, the result of a multi-map. Analyzer SPARK063 applies the same rule.
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
    /// The fields <paramref name="task"/> declares <c>FieldIndexing.Search</c>: what Spark's search treats as
    /// searchable, and what puts the index on Lucene. Read from the index definition, so a generated companion, a
    /// translated field's per-language companion and a hand-written <c>Index(x =&gt; x.F, FieldIndexing.Search)</c>
    /// are found the same way.
    /// </summary>
    public static IReadOnlyList<string> SearchableFields(IAbstractIndexCreationTask task)
    {
        // Building a definition needs conventions. The task's own are restored afterwards so deployment still
        // hands it the store's (a custom collection-name convention changes the map).
        var conventions = task.Conventions;
        task.Conventions ??= new DocumentConventions();
        try
        {
            return [.. task.CreateIndexDefinition().Fields
                .Where(f => f.Value?.Indexing == FieldIndexing.Search)
                .Select(f => f.Key)
                .OrderBy(f => f, StringComparer.Ordinal)];
        }
        finally
        {
            task.Conventions = conventions;
        }
    }

    /// <summary>
    /// Sets <see cref="SearchEngineType.Lucene"/> on every task that has a searchable field or an explicit
    /// <c>[Fuzzy]</c> opt-in (<see cref="RequiresLucene"/>) and does not choose an engine itself.
    /// </summary>
    /// <remarks>
    /// An index that pins another engine keeps it when it is only searchable: that is the author's opt-out at index
    /// level, and Spark's search is then simply not fuzzy on it (<see cref="RunsLuceneAsync"/>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// An index with an explicit <c>[Fuzzy]</c> field pins another engine in its constructor, or cannot have its
    /// engine set. Every fuzzy query on it would throw, so this is a declaration error that stops startup rather than
    /// a choice to override silently.
    /// </exception>
    public static void Apply(IEnumerable<IAbstractIndexCreationTask> tasks, IReadOnlySet<Type> fuzzyIndexTypes)
    {
        foreach (var task in tasks)
        {
            var optedIn = RequiresLucene(task.GetType(), fuzzyIndexTypes);
            if (!optedIn && task.SearchEngineType is not (null or SearchEngineType.None)) continue;
            if (!optedIn && !HasSearchableField(task)) continue;

            switch (task.SearchEngineType)
            {
                case SearchEngineType.Lucene:
                    continue;

                case null or SearchEngineType.None:
                    if (task is not AbstractIndexCreationTaskBase<IndexDefinition> settable)
                    {
                        if (!optedIn) continue;
                        throw new InvalidOperationException(
                            $"Index '{task.IndexName}' ({task.GetType().FullName}) has a [Fuzzy] field but is not an index " +
                            "task whose search engine Spark can set. Set SearchEngineType = SearchEngineType.Lucene in its constructor.");
                    }
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
    /// Whether <paramref name="task"/> has a searchable field. A definition that cannot be built answers no: the
    /// deployment that follows reports it, per assembly, as it always has, instead of this check stopping startup.
    /// </summary>
    private static bool HasSearchableField(IAbstractIndexCreationTask task)
    {
        try
        {
            return SearchableFields(task).Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The fields of <paramref name="indexType"/> Spark's search matches fuzzily: its searchable fields
    /// (<see cref="SearchableFields"/>) minus those opted out with <c>[Fuzzy(false)]</c>. Empty for an index Spark
    /// cannot instantiate (no parameterless constructor) or whose definition cannot be built.
    /// </summary>
    /// <remarks>
    /// The opt-out is read from <paramref name="projectionType"/> and from the type arguments of the index's base
    /// (the mapped document, a multi-map's result). A field matches by its own name, by the name it is the
    /// <c>{Name}Search</c> companion of, and by the <c>TranslatedString</c> property a per-language field
    /// (<c>{Prop}_{lang}</c>) fans out of, so an opt-out on <c>Name</c> covers <c>NameSearch</c> too.
    /// Whether the index actually runs Lucene is a separate, server-side question (<see cref="RunsLuceneAsync"/>).
    /// </remarks>
    public static IReadOnlySet<string> FuzzySearchFields(Type indexType, Type? projectionType)
    {
        if (indexType.IsAbstract || indexType.GetConstructor(Type.EmptyTypes) is null) return EmptyFields;

        IReadOnlyList<string> searchable;
        try
        {
            searchable = SearchableFields((IAbstractIndexCreationTask)Activator.CreateInstance(indexType)!);
        }
        catch (Exception)
        {
            // A definition that cannot be built here could not have been deployed either; no fuzzy clauses.
            return EmptyFields;
        }
        if (searchable.Count == 0) return EmptyFields;

        var declaringTypes = new List<Type>();
        if (projectionType is not null) declaringTypes.Add(projectionType);
        for (var current = indexType.BaseType; current is not null; current = current.BaseType)
            if (current.IsGenericType)
                declaringTypes.AddRange(current.GetGenericArguments().Where(a => a.IsClass));

        var optedOut = declaringTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.GetCustomAttribute<FuzzyAttribute>(inherit: true) is { Enabled: false })
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        return searchable
            .Where(field => !OwnerNames(field).Any(optedOut.Contains))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The property names a field can belong to: itself, its companion base, its translated property.</summary>
    private static IEnumerable<string> OwnerNames(string field)
    {
        yield return field;

        var baseName = field;
        if (field.Length > SearchCompanionSuffix.Length && field.EndsWith(SearchCompanionSuffix, StringComparison.Ordinal))
        {
            baseName = field[..^SearchCompanionSuffix.Length];
            yield return baseName;
        }

        // {Prop}_{lang}: a property name may contain '_', so every '_' is a candidate separator.
        for (var underscore = baseName.IndexOf('_'); underscore > 0; underscore = baseName.IndexOf('_', underscore + 1))
            yield return baseName[..underscore];
    }

    /// <summary>Kept in lockstep with <c>IndexNaming.SearchCompanion</c>.</summary>
    private const string SearchCompanionSuffix = "Search";

    private static readonly IReadOnlySet<string> EmptyFields = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether index <paramref name="indexName"/> of <paramref name="database"/> currently runs Lucene, as the server
    /// reports it. A confirmed Lucene index is remembered for the process; any other answer (another engine, a
    /// replacement still building, an error) is asked again after <see cref="RecheckAfter"/>.
    /// </summary>
    /// <remarks>
    /// This is what keeps a deploy safe: the definition says Lucene the moment it is deployed, but an existing index
    /// keeps serving on its old engine until its side-by-side replacement swaps in, and a fuzzy clause against Corax
    /// fails the whole query. Until the server confirms, Spark's search stays substring-only.
    /// </remarks>
    public static async Task<bool> RunsLuceneAsync(IDocumentStore store, string? database, string indexName, CancellationToken cancellationToken = default)
    {
        database ??= store.Database;
        var key = (store.Identifier ?? string.Empty, database ?? string.Empty, indexName);
        if (engineChecks.TryGetValue(key, out var known)
            && (known.Lucene || DateTime.UtcNow - known.CheckedAt < RecheckAfter))
            return known.Lucene;

        bool lucene;
        try
        {
            var maintenance = database is null ? store.Maintenance : store.Maintenance.ForDatabase(database);
            var stats = await maintenance.SendAsync(new GetIndexStatisticsOperation(indexName), cancellationToken);
            lucene = stats?.SearchEngineType == SearchEngineType.Lucene;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            lucene = false;
        }

        engineChecks[key] = (lucene, DateTime.UtcNow);
        return lucene;
    }

    /// <summary>How long a "not Lucene (yet)" answer of <see cref="RunsLuceneAsync"/> is trusted.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<(string Store, string Database, string Index), (bool Lucene, DateTime CheckedAt)> engineChecks = new();

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
