using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Services;

/// <summary>RavenDB's <see cref="ISparkIndexDeployment"/>: <c>IndexCreation.CreateIndexes</c>, per assembly.</summary>
[Register(typeof(ISparkIndexDeployment), ServiceLifetime.Singleton)]
internal sealed partial class RavenIndexDeployment : ISparkIndexDeployment
{
    [Inject] private readonly IDocumentStore store;

    public void Deploy(IReadOnlyList<Assembly> assemblies, Func<Type, bool>? deploymentFilter)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                if (deploymentFilter is null)
                {
                    IndexCreation.CreateIndexes(assembly, store);
                    Console.WriteLine($"RavenDB indexes created/updated from assembly: {assembly.GetName().Name}");
                    continue;
                }

                var candidates = DeployableIndexTypes(assembly);
                var tasks = candidates
                    .Where(deploymentFilter)
                    .Select(t => (IAbstractIndexCreationTask)Activator.CreateInstance(t)!)
                    .ToList();

                if (tasks.Count > 0)
                    IndexCreation.CreateIndexes(tasks, store);
                Console.WriteLine(
                    $"RavenDB indexes created/updated from assembly: {assembly.GetName().Name} " +
                    $"({tasks.Count} of {candidates.Count}, deployment filter set)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating RavenDB indexes from {assembly.GetName().Name}: {ex.Message}");
            }
        }
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
