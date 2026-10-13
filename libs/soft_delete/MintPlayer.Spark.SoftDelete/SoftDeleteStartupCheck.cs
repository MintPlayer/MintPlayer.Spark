using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Indexes;


namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// What <c>AddSoftDelete()</c> checks when <c>UseSpark()</c> runs, so a mistake shows at startup
/// instead of at the first request (the filter policy's member access is otherwise only resolved
/// when a query first asks for it — the M2 risk).
/// </summary>
/// <remarks>
/// <para><b>Errors</b> (refuse startup): a model type implementing <see cref="ISoftDeletable"/>
/// whose four members are not public read/write instance properties of the interface's types. The
/// filter is rebound by member name, and RavenDB stores only public properties.</para>
/// <para>Installs the raw-delete guard (<see cref="SoftDeleteRawDeleteGuard"/>).</para>
/// <para><b>Warnings</b>:
/// an index over a
/// soft-deletable collection whose projection lacks <c>IsDeleted</c> (the filter falls back to
/// filtering after materialization) or whose Map never mentions <c>IsDeleted</c> (a pushed-down
/// <c>IsDeleted != true</c> matches every row of the index, deleted ones included, and the rows are
/// only dropped on the reload — pages come back short).</para>
/// </remarks>
internal static class SoftDeleteStartupCheck
{
    private static readonly (string Name, Type Type)[] Members =
    [
        (nameof(ISoftDeletable.IsDeleted), typeof(bool)),
        (nameof(ISoftDeletable.DeletedAt), typeof(DateTimeOffset?)),
        (nameof(ISoftDeletable.DeletedBy), typeof(string)),
        (nameof(ISoftDeletable.DeleteReason), typeof(string)),
    ];

    public static void Run(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SoftDeleteStartupCheck).FullName!);
        var modelLoader = services.GetRequiredService<IModelLoader>();

        var softDeletable = modelLoader.GetEntityTypes()
            .Select(definition => SoftDeleteTypes.Resolve(definition.ClrType))
            .Where(type => type is not null && typeof(ISoftDeletable).IsAssignableFrom(type))
            .Select(type => type!)
            .Distinct()
            .ToList();

        var problems = softDeletable.SelectMany(MemberProblems).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Soft deletion cannot govern these types:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));

        // A raw session delete of a soft-deletable row would skip the replacement (#467, D32).
        if (services.GetService<Raven.Client.Documents.IDocumentStore>() is { } store)
            SoftDeleteRawDeleteGuard.Install(store, softDeletable);

        if (services.GetService<IIndexCatalog>() is { } catalog)
        {
            var documentStore = services.GetService<Raven.Client.Documents.IDocumentStore>();

            // EntityTypes rather than CollectionType: a multi-map default names no collection of its own but
            // serves every type its [DefaultIndex(...)] lists (docs/default_index_PRD.md, SP1), and skipping
            // it hid exactly the short-page problem these warnings exist for.
            foreach (var entry in catalog.GetAllEntries())
            foreach (var entityType in entry.EntityTypes)
            {
                if (!typeof(ISoftDeletable).IsAssignableFrom(entityType))
                    continue;

                if (entry.ProjectionType is { } projection && projection.GetProperty(nameof(ISoftDeletable.IsDeleted)) is null)
                    logger.LogWarning(
                        "Projection {Projection} of index {Index} has no IsDeleted, so soft deletion of {EntityType} is " +
                        "applied after materialization for queries on it: add IsDeleted to the projection and the Map.",
                        projection.Name, entry.IndexName, entityType.Name);

                // A single-map has one map; a multi-map is judged by the map over this type's collection.
                var collection = entry.CollectionType is null ? documentStore?.Conventions.GetCollectionName(entityType) : null;
                if (!MapMentionsIsDeleted(entry.IndexType, collection))
                    logger.LogWarning(
                        "Index {Index} over soft-deletable {EntityType} does not emit IsDeleted in its Map. A filter pushed " +
                        "down to it reads the field as absent, which 'IsDeleted != true' matches for every row: deleted rows " +
                        "are only dropped after the reload and pages come back short. Emit IsDeleted in the Map.",
                        entry.IndexName, entityType.Name);
            }
        }
    }

    private static IEnumerable<string> MemberProblems(Type type)
    {
        foreach (var (name, memberType) in Members)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                yield return $"{type.FullName}.{name} is not a public property (an explicit interface implementation is stored nowhere).";
            else if (property.PropertyType != memberType)
                yield return $"{type.FullName}.{name} is {property.PropertyType.Name}, expected {memberType.Name}.";
            else if (!property.CanRead || !property.CanWrite || property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
                yield return $"{type.FullName}.{name} must have a public getter and setter.";
        }
    }

    /// <param name="collection">For a multi-map, the collection whose map is judged; <see langword="null"/> judges every map.</param>
    private static bool MapMentionsIsDeleted(Type indexType, string? collection)
    {
        try
        {
            if (Activator.CreateInstance(indexType) is not IAbstractIndexCreationTask task)
                return true;
            var definition = task.CreateIndexDefinition();
            return definition.Maps
                .Where(map => collection is null || string.Equals(SparkMapScanner.CollectionOf(map), collection, StringComparison.OrdinalIgnoreCase))
                .Any(map => map.Contains(nameof(ISoftDeletable.IsDeleted), StringComparison.Ordinal));
        }
        catch
        {
            // An index that cannot be built here is SPARK018's and the deployment's business, not ours.
            return true;
        }
    }
}
