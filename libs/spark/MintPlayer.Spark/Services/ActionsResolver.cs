using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using System.Reflection;

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

    /// <summary>
    /// Resolves an Actions class by the model type's <b>name</b> alone — the path for JSON-only
    /// virtual types, which have no CLR entity to close <see cref="IPersistentObjectActions{T}"/>
    /// over. Finds <c>{entityTypeName}Actions</c> the same way the typed path does; returns
    /// <see langword="null"/> when no such class exists (unlike the typed path, there is no
    /// default to fall back to — a virtual type without actions has no behavior at all).
    /// </summary>
    object? ResolveByEntityName(string entityTypeName);
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

    public object? ResolveByEntityName(string entityTypeName)
    {
        var actionsType = FindActionsType($"{entityTypeName}Actions");
        if (actionsType is null) return null;
        return serviceProvider.GetService(actionsType)
            ?? ActivatorUtilities.CreateInstance(serviceProvider, actionsType);
    }

    public object ResolveForType(Type entityType)
    {
        // Cache the closed Resolve<TEntity>() MethodInfo per entity type — the reflective
        // GetMethod + MakeGenericMethod was previously hit on every dispatch.
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo>(
            ("ActionsResolver.Resolve", entityType),
            static k =>
            {
                var method = typeof(ActionsResolver).GetMethod(nameof(Resolve))!;
                return method.MakeGenericMethod(k.Type);
            });
        return genericMethod.Invoke(this, null)!;
    }

    private static Type? FindActionsType(string typeName)
    {
        // Negative caching matters here: when no Actions class exists for an entity, the
        // resolver still walks every loaded assembly on every request unless the null
        // is cached. ReflectionCache caches null too via Lazy<object?>.
        return ReflectionCache.GetOrAdd<Type?>(
            $"actionsType|{typeName}",
            () =>
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
            });
    }
}
