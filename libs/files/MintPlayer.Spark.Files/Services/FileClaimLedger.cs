using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// What one request's saves decided about files, per entity instance, between the three moments that act on it:
/// <see cref="SparkFileValueHandler.WriteAsync"/> (decide), <see cref="SparkFileValueHandler.OnStoredAsync"/> (move bytes, in
/// the save's unit of work) and <see cref="FileStoreInterceptor"/> (after the commit: deletes, and the developer's hook).
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class FileClaimLedger
{
    private readonly Dictionary<object, Pending> pending = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, List<AcceptedFile>> accepted = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, List<string>> deletes = new(ReferenceEqualityComparer.Instance);

    /// <summary>A claimed upload whose staged bytes become the entity's file, with its image variants (M8).</summary>
    internal sealed record Attach(string FileId, string StagingKey, AcceptedFile Accepted, IReadOnlyList<Transfer> Variants);

    /// <summary>One more object an attach moves: a variant's staged key and the file id it gets on the entity.</summary>
    internal sealed record Transfer(string FileId, string StagingKey);

    /// <summary>A claimed file, for <see cref="IFileUploadHandler.OnFileAcceptedAsync"/> once the save committed.</summary>
    internal sealed record AcceptedFile(EntityAttributeDefinition Attribute, SparkFile File, string UserId);

    internal sealed class Pending
    {
        public List<Attach> Attaches { get; } = [];
        public List<string> Detaches { get; } = [];

        /// <summary>
        /// The files the entity's AsDetail rows held when its save began, per attribute path (<c>ServiceEntries.Invoice</c>),
        /// or <see langword="null"/> when it has none (#700 M10). What a row may keep, and what is removed when no row holds it
        /// any more.
        /// </summary>
        public Dictionary<string, List<SparkFile>>? Snapshot { get; set; }

        /// <summary>The snapshot files a row kept, per path: across rows, so two rows cannot keep one file.</summary>
        public Dictionary<string, HashSet<string>> NestedKept { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The decisions for <paramref name="entity"/>, created on first use.</summary>
    internal Pending For(object entity)
    {
        if (!pending.TryGetValue(entity, out var entry))
            pending[entity] = entry = new Pending();
        return entry;
    }

    /// <summary>Takes the decisions for <paramref name="entity"/>; the claimed files wait for the commit.</summary>
    internal Pending? TakePending(object entity)
    {
        if (!pending.Remove(entity, out var entry))
            return null;
        if (entry.Attaches.Count > 0)
            accepted[entity] = entry.Attaches.Select(a => a.Accepted).ToList();
        return entry;
    }

    /// <summary>Takes the files <paramref name="entity"/>'s committed save claimed.</summary>
    internal IReadOnlyList<AcceptedFile> TakeAccepted(object entity)
        => accepted.Remove(entity, out var files) ? files : [];

    /// <summary>Store keys to delete once <paramref name="entity"/>'s save committed (a store outside the session).</summary>
    internal void DeleteAfterCommit(object entity, IEnumerable<string> keys)
    {
        if (!deletes.TryGetValue(entity, out var list))
            deletes[entity] = list = [];
        list.AddRange(keys);
    }

    /// <summary>Takes the keys <paramref name="entity"/>'s committed save no longer needs.</summary>
    internal IReadOnlyList<string> TakeDeletes(object entity)
        => deletes.Remove(entity, out var keys) ? keys : [];
}
