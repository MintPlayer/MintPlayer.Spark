using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Service for resolving Actions classes for entity types.
/// </summary>
public interface IActionsResolver
{
    /// <summary>
    /// Resolves the Actions class for a specific entity type.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <returns>The resolved actions instance</returns>
    IPersistentObjectActions<T> Resolve<T>() where T : class;

    /// <summary>
    /// Resolves the Actions class for a runtime entity type.
    /// </summary>
    /// <param name="entityType">The entity type</param>
    /// <returns>The resolved actions instance as an object</returns>
    object ResolveForType(Type entityType);
}

[Register(typeof(IActionsResolver), ServiceLifetime.Scoped)]
internal partial class ActionsResolver : IActionsResolver
{
    [Inject] private readonly IServiceProvider serviceProvider;

    public IPersistentObjectActions<T> Resolve<T>() where T : class
    {
        var typeName = typeof(T).Name;

        // 1. Try entity-specific actions (e.g., PersonActions)
        var actionsType = FindActionsType($"{typeName}Actions");
        if (actionsType != null)
        {
            var actions = serviceProvider.GetService(actionsType)
                ?? ActivatorUtilities.CreateInstance(serviceProvider, actionsType);
            if (actions is IPersistentObjectActions<T> typedActions)
                return typedActions;
        }

        // 2. Try app's registered IPersistentObjectActions<T>
        var appDefault = serviceProvider.GetService<IPersistentObjectActions<T>>();
        if (appDefault != null)
            return appDefault;

        // 3. Fall back to library's DefaultPersistentObjectActions<T>
        return ActivatorUtilities.CreateInstance<DefaultPersistentObjectActions<T>>(serviceProvider);
    }

    public object ResolveForType(Type entityType)
    {
        var method = typeof(ActionsResolver).GetMethod(nameof(Resolve))!;
        var genericMethod = method.MakeGenericMethod(entityType);
        return genericMethod.Invoke(this, null)!;
    }

    private Type? FindActionsType(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == typeName && !t.IsAbstract && !t.IsInterface);
                if (type != null) return type;
            }
            catch (ReflectionTypeLoadException)
            {
                // Skip assemblies that can't be loaded
                continue;
            }
        }
        return null;
    }
}
