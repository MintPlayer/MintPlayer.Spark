using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;

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
/// <para><b>Warnings</b>: an Actions class of a soft-deletable type that overrides
/// <c>OnDeleteAsync</c> (not called on a delete any more — only on a purge); an index over a
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

        using (var scope = services.CreateScope())
        {
            var actionsResolver = scope.ServiceProvider.GetService<IActionsResolver>();
            foreach (var type in softDeletable)
            {
                if (actionsResolver is not null && OverridesOnDelete(actionsResolver, type) is { } actionsType)
                    logger.LogWarning(
                        "{ActionsType} overrides OnDeleteAsync, but {EntityType} is ISoftDeletable: a delete is replaced by " +
                        "a soft delete before the Actions class is asked, so the override runs only for a purge. Move " +
                        "delete-time logic to OnBeforeDeleteAsync or an ISoftDeleteObserver.",
                        actionsType.Name, type.Name);
            }
        }

        if (services.GetService<IIndexCatalog>() is { } catalog)
        {
            foreach (var entry in catalog.GetAllEntries())
            {
                if (entry.CollectionType is null || !typeof(ISoftDeletable).IsAssignableFrom(entry.CollectionType))
                    continue;

                if (entry.ProjectionType is { } projection && projection.GetProperty(nameof(ISoftDeletable.IsDeleted)) is null)
                    logger.LogWarning(
                        "Projection {Projection} of index {Index} has no IsDeleted, so soft deletion of {EntityType} is " +
                        "applied after materialization for queries on it: add IsDeleted to the projection and the Map.",
                        projection.Name, entry.IndexName, entry.CollectionType.Name);

                if (!MapMentionsIsDeleted(entry.IndexType))
                    logger.LogWarning(
                        "Index {Index} over soft-deletable {EntityType} does not emit IsDeleted in its Map. A filter pushed " +
                        "down to it reads the field as absent, which 'IsDeleted != true' matches for every row: deleted rows " +
                        "are only dropped after the reload and pages come back short. Emit IsDeleted in the Map.",
                        entry.IndexName, entry.CollectionType.Name);
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

    private static Type? OverridesOnDelete(IActionsResolver resolver, Type entityType)
    {
        object actions;
        try
        {
            actions = resolver.ResolveForType(entityType);
        }
        catch
        {
            return null;
        }

        var method = actions.GetType().GetMethod("OnDeleteAsync", [typeof(IAsyncDocumentSession), typeof(string)]);
        var declaring = method?.DeclaringType;
        if (declaring is null || (declaring.IsGenericType && declaring.GetGenericTypeDefinition() == typeof(MintPlayer.Spark.Actions.DefaultPersistentObjectActions<>)))
            return null;
        return actions.GetType();
    }

    private static bool MapMentionsIsDeleted(Type indexType)
    {
        try
        {
            if (Activator.CreateInstance(indexType) is not IAbstractIndexCreationTask task)
                return true;
            var definition = task.CreateIndexDefinition();
            return definition.Maps.Any(map => map.Contains(nameof(ISoftDeletable.IsDeleted), StringComparison.Ordinal));
        }
        catch
        {
            // An index that cannot be built here is SPARK018's and the deployment's business, not ours.
            return true;
        }
    }
}
