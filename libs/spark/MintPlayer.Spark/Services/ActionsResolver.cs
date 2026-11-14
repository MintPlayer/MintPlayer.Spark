using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
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
                return Attach(typedActions);

            // F3. A class named for this entity exists but does not implement the contract for it.
            // Falling through to the permissive default here is the worst available outcome: the
            // author's IsAllowedAsync and GetRowFilterAsync are never consulted, IsOverridden reports
            // false for both, HasRowRule reports false, and the type is served UNRESTRICTED with no
            // diagnostic anywhere. The most likely cause is the one that makes it dangerous — the
            // generic argument drifted after a rename, so the file still reads as though it guards
            // the type.
            throw new InvalidOperationException(
                $"'{actionsType.FullName}' is named for entity '{typeName}' but does not implement " +
                $"'IPersistentObjectActions<{typeName}>'. It therefore cannot serve as that entity's " +
                $"actions class, and using the framework default instead would silently drop any row " +
                $"filter, per-row rule or redaction the class declares. Either make it derive from " +
                $"'DefaultPersistentObjectActions<{typeName}>' (or implement " +
                $"'IPersistentObjectActions<{typeName}>'), or rename it so it no longer claims to be " +
                $"'{typeName}'s actions class.");
        }

        // 2. Try app's registered IPersistentObjectActions<T>
        var appDefault = serviceProvider.GetService<IPersistentObjectActions<T>>();
        if (appDefault != null)
            return Attach(appDefault);

        // 3. Fall back to library's DefaultPersistentObjectActions<T>
        return Attach(ActivatorUtilities.CreateInstance<DefaultPersistentObjectActions<T>>(serviceProvider));
    }

    /// <summary>
    /// Hands the base class its framework services (the session, row security, the collection
    /// guard, …) AFTER construction — so a consumer's hand-written constructor never has to
    /// thread framework plumbing just because the base pipeline needs it.
    /// </summary>
    private IPersistentObjectActions<T> Attach<T>(IPersistentObjectActions<T> actions) where T : class
    {
        if (actions is DefaultPersistentObjectActions<T> withPipeline)
            withPipeline.Attach(serviceProvider);
        return actions;
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

    /// <summary>
    /// The actions class named for <paramref name="entityType"/> (<c>{Entity}Actions</c>), without creating
    /// one: null when the type has none and is served by a registered or the default actions class.
    /// </summary>
    internal static Type? FindActionsTypeFor(Type entityType) => FindActionsType($"{entityType.Name}Actions");

    private static Type? FindActionsType(string typeName)
    {
        // Negative caching matters here: when no Actions class exists for an entity, the
        // resolver still walks every loaded assembly on every request unless the null
        // is cached. ReflectionCache caches null too via Lazy<object?>.
        return ReflectionCache.GetOrAdd<Type?>(
            $"actionsType|{typeName}",
            () =>
            {
                // F3. Collect every match rather than taking the first. Returning the first made
                // resolution depend on assembly enumeration order — which is not deterministic and
                // is not something an author controls — and the answer was then cached for the
                // process lifetime. Two classes with the same simple name in different namespaces
                // therefore resolved to whichever the CLR happened to load first, so a duplicate
                // could displace the real actions class and silently unrestrict its entity.
                var matches = new List<Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        matches.AddRange(assembly.GetTypes()
                            .Where(t => t.Name == typeName && !t.IsAbstract && !t.IsInterface));
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // Skip assemblies that can't be loaded
                        continue;
                    }
                }

                if (matches.Count > 1)
                {
                    // #660 G17: an app (or a downstream library) that specialises a library's actions class under
                    // the same name is recorded by the generator as [assembly: SparkActionsOverride]. It wins only
                    // when every other match is one of its own base classes.
                    if (ChooseOverride(matches, ReadOverrides()) is { } chosen)
                        return chosen;

                    throw new InvalidOperationException(
                        $"'{typeName}' is declared by more than one loaded assembly, so the actions " +
                        $"class for this entity is ambiguous: " +
                        string.Join(", ", matches.Select(t => $"'{t.FullName}' ({t.Assembly.GetName().Name})")) +
                        ". Resolution used to pick whichever assembly loaded first and cache it for the " +
                        "process lifetime, which could silently substitute one type's actions class for " +
                        "another's. Rename all but one, register the intended one explicitly as " +
                        $"'IPersistentObjectActions<>' so it is chosen by contract rather than by name, or, to " +
                        "specialise a library's actions class, derive from it in a project that runs the Spark " +
                        "source generator (it records the override as [assembly: SparkActionsOverride]).");
                }

                return matches.Count == 1 ? matches[0] : null;
            });
    }

    /// <summary>
    /// The match a <see cref="SparkActionsOverrideAttribute"/> names and every other match is a base class of; null
    /// when there is no such match (the caller refuses the ambiguity). A chain (library → downstream library → app,
    /// each recorded) resolves to its most-derived class; two siblings that both specialise one library class do not.
    /// </summary>
    internal static Type? ChooseOverride(IReadOnlyList<Type> matches, IEnumerable<SparkActionsOverrideAttribute> overrides)
    {
        var candidates = overrides
            .Select(o => o.ActionsType)
            .Where(matches.Contains)
            .Distinct()
            .Where(candidate => matches.All(m => m == candidate || m.IsAssignableFrom(candidate)))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static IEnumerable<SparkActionsOverrideAttribute> ReadOverrides()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            SparkActionsOverrideAttribute[] attributes;
            try
            {
                attributes = [.. assembly.GetCustomAttributes<SparkActionsOverrideAttribute>()];
            }
            catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException or TypeLoadException)
            {
                continue;
            }
            foreach (var attribute in attributes)
                yield return attribute;
        }
    }
}
