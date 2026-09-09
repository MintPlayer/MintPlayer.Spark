using MintPlayer.Spark.Abstractions.Model;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Refuses to start while any stored embedded row is missing its key.
/// </summary>
/// <remarks>
/// A row key is what lets a save match an incoming row against the stored collection. Without one
/// the save cannot tell an edit from a delete plus a create, so a row is rebuilt from scratch —
/// losing every value the payload is not allowed to write — and the row type's New/Edit/Delete
/// rights become undecidable.
/// <para>
/// ⚠️ <b>This check cannot be done in the running application.</b> The key is minted by a field
/// initializer that runs during deserialization, so a row stored without one comes back carrying a
/// fresh guid — a different one on every load. In memory every row looks keyed. Only the raw JSON
/// knows, which is why this asks the database.
/// </para>
/// <para>
/// ⚠️ It also cannot merely warn. A keyless row is not inert: loading the document marks it dirty,
/// so the next unrelated <c>SaveChanges</c> in that session writes random ids into a document nobody
/// edited. An app that starts anyway corrupts identity quietly, at a moment unrelated to the
/// mistake.
/// </para>
/// <para>
/// The companion check — "is a type that should be a value object missing its marker" — is not here.
/// SPARK017 answers it at compile time, from the same <c>SparkContext</c> roots, where it can fail
/// the build instead of a deployment.
/// </para>
/// </remarks>
internal static class ValueObjectKeyVerifier
{
    /// <summary>One embedded collection to check: where it lives, and what its key is called.</summary>
    private readonly record struct KeyedCollection(string Collection, string Path, string KeyProperty);

    public static async Task VerifyAsync(
        Type contextType, IDocumentStore store, Action<string> log, CancellationToken cancellationToken)
    {
        var targets = Discover(contextType, store);
        if (targets.Count == 0)
            return;

        var offenders = new List<string>();

        using var session = store.OpenAsyncSession();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An absent field DOES match `== null` — unlike `== false`, which an absent field does
            // not match. The empty-string arm covers a key whose default is "" rather than a guid:
            // ProjectColumn and EventColumnMapping both derive theirs, so a row saved before the
            // deriving hook existed holds a present-but-useless key.
            var rql =
                $"from '{target.Collection}' " +
                $"where {target.Path}[].{target.KeyProperty} == null " +
                $"or {target.Path}[].{target.KeyProperty} == ''";

            var query = session.Advanced.AsyncRawQuery<object>(rql)
                .Statistics(out var statistics)
                // The first run on a large collection builds an auto-index, and a stale result is
                // an empty result — which would read as "nothing to fix" and let startup proceed.
                .WaitForNonStaleResults(TimeSpan.FromMinutes(2))
                .Take(0);

            await query.ToListAsync(cancellationToken);

            if (statistics.TotalResults > 0)
                offenders.Add($"{target.Collection}.{target.Path} ({statistics.TotalResults} document(s))");
        }

        if (offenders.Count == 0)
        {
            // Say so. Silence makes "the gate ran and passed" indistinguishable from "the gate never
            // ran", which is the failure mode that lets a check rot unnoticed.
            log($"Spark: row keys verified across {targets.Count} embedded collection(s).");
            return;
        }

        throw new InvalidOperationException(
            "Spark refuses to start: stored embedded rows are missing their row key in "
            + string.Join(", ", offenders)
            + ". A row without a key cannot be matched when its parent is saved, so the row is "
            + "rebuilt from the payload and any value the payload may not write is lost. Run the "
            + "backfill migration for these collections before starting. This cannot be detected "
            + "at runtime — a keyless row deserializes with a freshly minted key, so it looks "
            + "correct in memory.");
    }

    /// <summary>
    /// Every embedded collection of a registered value object reachable from the context's
    /// <c>IRavenQueryable&lt;T&gt;</c> properties, with the RQL path to it.
    /// </summary>
    private static List<KeyedCollection> Discover(Type contextType, IDocumentStore store)
    {
        var found = new List<KeyedCollection>();

        foreach (var root in RootTypes(contextType))
        {
            var collection = store.Conventions.FindCollectionName(root);
            Walk(root, collection, prefix: null, found, new HashSet<Type>());
        }

        // The same element type can hang off several roots; each is a separate query.
        return [.. found.Distinct()];
    }

    private static IEnumerable<Type> RootTypes(Type contextType)
        => contextType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType
                && t.GetGenericTypeDefinition().Name.StartsWith("IRavenQueryable", StringComparison.Ordinal))
            .Select(t => t.GetGenericArguments()[0]);

    private static void Walk(
        Type owner, string collection, string? prefix, List<KeyedCollection> found, HashSet<Type> visiting)
    {
        // Guard the cycle, not the repeat: the same type under two different paths is two different
        // collections to check.
        if (!visiting.Add(owner))
            return;

        foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length > 0)
                continue;

            var path = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            var elementType = CollectionElementType(property.PropertyType);

            if (elementType is not null)
            {
                if (SparkValueObjects.GetKeyPropertyName(elementType) is { } key)
                    found.Add(new KeyedCollection(collection, path, key));

                Walk(elementType, collection, path, found, visiting);
            }
            else if (IsEmbeddable(property.PropertyType))
            {
                // A single nested object carries no key — it is matched by its property name — but a
                // collection below it still does.
                Walk(property.PropertyType, collection, path, found, visiting);
            }
        }

        visiting.Remove(owner);
    }

    private static Type? CollectionElementType(Type type)
    {
        if (type == typeof(string) || !IsEmbeddableContainer(type))
            return null;

        if (type.IsArray)
            return IsEmbeddable(type.GetElementType()!) ? type.GetElementType() : null;

        var enumerable = type.GetInterfaces().Concat([type]).FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        var element = enumerable?.GetGenericArguments()[0];
        return element is not null && IsEmbeddable(element) ? element : null;
    }

    /// <summary>⚠️ Dictionaries are excluded: their element is a <c>KeyValuePair</c>, not a row.</summary>
    private static bool IsEmbeddableContainer(Type type)
        => !type.GetInterfaces().Concat([type]).Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && i.GetGenericArguments()[0].IsGenericType
            && i.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>));

    private static bool IsEmbeddable(Type type)
        => type.IsClass
        && type != typeof(string)
        && !type.IsAbstract
        && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;
}
