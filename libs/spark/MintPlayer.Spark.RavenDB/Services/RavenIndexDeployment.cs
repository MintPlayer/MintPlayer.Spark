using System.Reflection;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Services;

/// <summary>RavenDB's <see cref="ISparkIndexDeployment"/>: <c>IndexCreation.CreateIndexes</c>, per assembly.</summary>
[Register(typeof(ISparkIndexDeployment), ServiceLifetime.Singleton)]
internal sealed partial class RavenIndexDeployment : ISparkIndexDeployment
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<RavenIndexDeployment> logger;

    public void Deploy(IReadOnlyList<Assembly> assemblies, Func<Type, bool>? deploymentFilter)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                if (deploymentFilter is null)
                {
                    IndexCreation.CreateIndexes(assembly, store);
                    logger.LogInformation("RavenDB indexes created/updated from assembly {Assembly}", assembly.GetName().Name);
                    continue;
                }

                var candidates = DeployableIndexTypes(assembly);
                var tasks = candidates
                    .Where(deploymentFilter)
                    .Select(t => (IAbstractIndexCreationTask)Activator.CreateInstance(t)!)
                    .ToList();

                if (tasks.Count > 0)
                    IndexCreation.CreateIndexes(tasks, store);
                logger.LogInformation(
                    "RavenDB indexes created/updated from assembly {Assembly} ({Deployed} of {Candidates}, deployment filter set)",
                    assembly.GetName().Name,
                    tasks.Count,
                    candidates.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not create the RavenDB indexes of assembly {Assembly}", assembly.GetName().Name);
            }
        }

        DeployViews(assemblies, deploymentFilter);
    }

    /// <summary>
    /// The <see cref="Abstractions.Views.SparkView{TRow}"/> declarations (#536 D32), rendered as static indexes. A view
    /// that a native index of the same name shadows is skipped, as the catalog skips it.
    /// </summary>
    /// <remarks>
    /// Each view deploys on its own (#716 D2): RavenDB applies a batch all or nothing, so in one batch a single refused
    /// view kept every other view out of the database, messaging's <c>SparkMessages_ByState</c> among them.
    /// </remarks>
    private void DeployViews(IReadOnlyList<Assembly> assemblies, Func<Type, bool>? deploymentFilter)
    {
        var nativeTypes = assemblies.SelectMany(DeployableIndexTypes).ToList();
        var native = nativeTypes.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Type> viewTypes;
        try
        {
            viewTypes = assemblies.SelectMany(Abstractions.Views.SparkViewEntries.ViewTypes)
                .Where(t => deploymentFilter?.Invoke(t) != false)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not find the Spark views to create as RavenDB indexes");
            return;
        }

        if (viewTypes.Count == 0)
            return;

        string[] existing;
        try
        {
            existing = store.Maintenance.Send(new GetIndexNamesOperation(0, int.MaxValue));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not list the RavenDB indexes, so no index a Spark view replaces is deleted");
            existing = [];
        }

        HashSet<string>? declared = null;
        var deployed = new List<string>();
        foreach (var viewType in viewTypes)
        {
            string? indexName = null;
            try
            {
                var view = Indexes.RavenViewIndexes.Render(viewType);
                indexName = view.IndexName;
                if (native.Contains(indexName))
                    continue;

                var replaced = existing.Where(name => Replaces(indexName, name)).ToList();
                if (replaced.Count > 0)
                {
                    declared ??= DeclaredIndexNames(nativeTypes);
                    foreach (var name in replaced.Where(name => !declared.Contains(name)))
                    {
                        store.Maintenance.Send(new DeleteIndexOperation(name));
                        logger.LogInformation(
                            "Deleted the RavenDB index {Replaced}: the Spark view {View} replaces it, and RavenDB keeps both names in one folder",
                            name,
                            indexName);
                    }
                }

                view.Execute(store);
                deployed.Add(indexName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not create the RavenDB index of Spark view {View}", indexName ?? viewType.FullName);
            }
        }

        if (deployed.Count > 0)
            logger.LogInformation("RavenDB indexes created/updated from {Count} Spark view(s): {Views}", deployed.Count, string.Join(", ", deployed));
    }

    /// <summary>
    /// Whether the view <paramref name="view"/> replaces the existing index <paramref name="existing"/> (#716 D1): a
    /// class-based index is named after its class with <c>_</c> turned into <c>/</c>, and RavenDB stores an index in a
    /// folder named after it with <c>/</c> turned into <c>_</c>. So the class <c>X_Y</c> deployed as <c>X/Y</c>, and the
    /// view <c>X_Y</c> that took the class's name is refused while <c>X/Y</c> exists ("would result in directory name
    /// collision"). While the class is still declared, it shadows the view, so in the normal case only a leftover matches.
    /// </summary>
    private static bool Replaces(string view, string existing)
        => !string.Equals(existing, view, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Replace('/', '_'), view, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The names the application's native index tasks deploy under, which a view never deletes: a task can name its index
    /// itself (an <c>IndexName</c> override), and then the class name no longer shadows the view.
    /// </summary>
    private static HashSet<string> DeclaredIndexNames(IEnumerable<Type> nativeTypes)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in nativeTypes)
        {
            try
            {
                names.Add(((IAbstractIndexCreationTask)Activator.CreateInstance(type)!).IndexName);
            }
            catch (Exception)
            {
                // RavenDB cannot create it either (IndexCreation instantiates it the same way), so it deploys no index.
            }
        }
        return names;
    }

    /// <summary>
    /// The types <c>IndexCreation.CreateIndexes(assembly, store)</c> would deploy from
    /// <paramref name="assembly"/>, by RavenDB's own criterion, so a filtered deployment is a strict
    /// subset of the unfiltered one rather than a second opinion about what an index is.
    /// </summary>
    /// <remarks>
    /// RavenDB 7.2 (<c>IndexCreation.GetAllInstancesOfType</c>): a non-abstract class implementing
    /// <see cref="IAbstractIndexCreationTask"/>, instantiated with <see cref="Activator.CreateInstance(Type)"/>
    /// — so a type without a public parameterless constructor fails the same way in both paths.
    /// Deliberately not <see cref="RavenIndexHierarchy.IsIndex"/>: that is Spark's discovery gate and
    /// covers only <c>AbstractIndexCreationTask</c>, while RavenDB also deploys JavaScript, counters
    /// and time-series index tasks.
    /// </remarks>
    internal static IReadOnlyList<Type> DeployableIndexTypes(Assembly assembly)
        => ReflectionCache.GetOrAdd<(string Op, Assembly Asm), IReadOnlyList<Type>>(
            ("RavenIndexDeployment.DeployableIndexTypes", assembly),
            static k => LoadableTypes(k.Asm)
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IAbstractIndexCreationTask).IsAssignableFrom(t))
                .ToArray());

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!);
        }
    }
}
