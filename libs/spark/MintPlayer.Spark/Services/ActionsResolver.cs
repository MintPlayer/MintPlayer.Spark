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
    /// Resolves the Actions class for a specific entity type, by contract (D28, X9): a registered
    /// <see cref="IPersistentObjectActions{T}"/>, else the one class implementing it for exactly
    /// <typeparamref name="T"/>, else <see cref="DefaultPersistentObjectActions{T}"/>.
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
    /// Resolves the Actions class of a JSON-only virtual type, which has no CLR entity to close
    /// <see cref="IPersistentObjectActions{T}"/> over (D28, X9): <c>{DeclaredName}Actions</c> inside the
    /// declaring library's assembly for a library type, so an application rename changes nothing and an
    /// application class of the same name is never picked; <c>{Name}Actions</c> outside the libraries
    /// that declare a type of that name for an application type. <see langword="null"/> when no such
    /// class exists (a virtual type without actions has no behavior at all).
    /// </summary>
    object? ResolveVirtual(EntityTypeDefinition definition);
}

[Register(typeof(IActionsResolver), ServiceLifetime.Scoped)]
internal partial class ActionsResolver : IActionsResolver
{
    [Inject] private readonly IServiceProvider serviceProvider;

    public IPersistentObjectActions<T> Resolve<T>() where T : class
    {
        // 1. A registration wins: the generated AddActions() makes one per actions class, and an
        //    application overrides a library's actions class this way, explicitly.
        var registered = serviceProvider.GetService<IPersistentObjectActions<T>>();
        if (registered != null)
            return Attach(registered);

        // 2. The class implementing the contract for exactly T. Never by simple name: two libraries'
        //    'OrderActions' for two different 'Order' types are not ambiguous, and a class named for an
        //    entity whose generic argument drifted can no longer pass for its actions class (F3).
        var actionsType = FindActionsTypeFor(typeof(T));
        if (actionsType != null)
        {
            var actions = serviceProvider.GetService(actionsType)
                ?? ActivatorUtilities.CreateInstance(serviceProvider, actionsType);
            return Attach((IPersistentObjectActions<T>)actions);
        }

        // 3. Fall back to library's DefaultPersistentObjectActions<T> — unless a class in T's own assembly is
        //    named for T without implementing the contract for it (F3, below).
        RefuseMisnamedActionsClass(typeof(T));
        return Attach(ActivatorUtilities.CreateInstance<DefaultPersistentObjectActions<T>>(serviceProvider));
    }

    /// <summary>
    /// F3. A class named <c>{Entity}Actions</c> beside the entity (in its assembly) that does not implement
    /// the contract for it. Falling through to the permissive default is the worst available outcome: the
    /// author's IsAllowedAsync and GetRowFilterAsync are never consulted, and the type is served UNRESTRICTED
    /// with no diagnostic anywhere. The likely cause is the one that makes it dangerous — the generic argument
    /// drifted after a rename, so the file still reads as though it guards the type. Only the entity's own
    /// assembly is searched: a library's <c>OrderActions</c> for its own <c>Order</c> says nothing about an
    /// application's <c>Order</c> (D28).
    /// </summary>
    private static void RefuseMisnamedActionsClass(Type entityType)
    {
        if (MisnamedActionsClass(entityType) is not { } misnamed) return;
        throw new InvalidOperationException(
            $"'{misnamed.FullName}' is named for entity '{entityType.Name}' but does not implement " +
            $"'IPersistentObjectActions<{entityType.Name}>'. It therefore cannot serve as that entity's " +
            $"actions class, and using the framework default instead would silently drop any row " +
            $"filter, per-row rule or redaction the class declares. Either make it derive from " +
            $"'DefaultPersistentObjectActions<{entityType.Name}>' (or implement " +
            $"'IPersistentObjectActions<{entityType.Name}>'), or rename it so it no longer claims to be " +
            $"'{entityType.Name}'s actions class.");
    }

    private static Type? MisnamedActionsClass(Type entityType)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("ActionsResolver.Misnamed", entityType),
            static k =>
            {
                var contract = typeof(IPersistentObjectActions<>).MakeGenericType(k.Type);
                var name = $"{k.Type.Name}Actions";
                return TypesOf(k.Type.Assembly).FirstOrDefault(t =>
                    t.Name == name && t is { IsClass: true, IsAbstract: false } && !contract.IsAssignableFrom(t));
            });

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

    public object? ResolveVirtual(EntityTypeDefinition definition)
    {
        var actionsType = FindVirtualActionsType(definition, serviceProvider.GetService<IModelLoader>()?.GetEntityTypes());
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
    /// The class implementing <see cref="IPersistentObjectActions{T}"/> for exactly <paramref name="entityType"/>,
    /// without creating one: null when there is none (a registered or the default actions class serves it).
    /// </summary>
    /// <remarks>
    /// Every non-abstract, non-generic class in the loaded assemblies is a candidate, except a private nested
    /// one (a local helper, used by whoever constructs or registers it). Several candidates resolve to the most
    /// derived when it derives from all the others (a new, explicit way to override a library's actions class);
    /// otherwise they are ambiguous.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Two unrelated classes implement the contract for the type.</exception>
    internal static Type? FindActionsTypeFor(Type entityType)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("ActionsResolver.ForType", entityType),
            static k =>
            {
                var contract = typeof(IPersistentObjectActions<>).MakeGenericType(k.Type);
                var candidates = CandidateTypes()
                    .Where(t => contract.IsAssignableFrom(t))
                    .Distinct()
                    .ToList();

                if (candidates.Count <= 1)
                    return candidates.FirstOrDefault();

                var mostDerived = candidates.FirstOrDefault(c => candidates.All(other => other == c || other.IsAssignableFrom(c)));
                if (mostDerived is not null)
                    return mostDerived;

                throw new InvalidOperationException(
                    $"More than one class implements 'IPersistentObjectActions<{k.Type.Name}>' ({k.Type.FullName}), and none " +
                    "derives from all the others, so the actions class for this entity is ambiguous: " +
                    string.Join(", ", candidates.Select(t => $"'{t.FullName}' ({t.Assembly.GetName().Name})")) +
                    ". Remove all but one, derive the one that should win from the others, or register the intended one " +
                    $"as 'IPersistentObjectActions<{k.Type.Name}>' (the generated AddActions() does) so it is chosen explicitly.");
            });

    /// <summary>
    /// The actions class of a virtual type (<see cref="IActionsResolver.ResolveVirtual"/>), without creating one.
    /// </summary>
    /// <param name="model">The model's types, to keep an application type's lookup out of the libraries that declare a type of the same name; none when omitted.</param>
    /// <exception cref="InvalidOperationException">The name is declared more than once where it is looked up.</exception>
    internal static Type? FindVirtualActionsType(EntityTypeDefinition definition, IEnumerable<EntityTypeDefinition>? model)
    {
        if (definition.DeclaringAssembly is { } library)
        {
            var declared = definition.DeclaredName ?? definition.Name;
            return FindActionsType($"{declared}Actions", [library], include: true);
        }

        // An application type: anywhere but in a library that declares a type of this name — that class
        // is the library's, and serves the library's type (renamed or not), never the application's.
        var claimed = (model ?? [])
            .Where(t => t.DeclaringAssembly is not null && string.Equals(t.DeclaredName ?? t.Name, definition.Name, StringComparison.Ordinal))
            .Select(t => t.DeclaringAssembly!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();
        return FindActionsType($"{definition.Name}Actions", claimed, include: false);
    }

    /// <summary>
    /// The startup half of the checks resolution makes on first use (commerce F10, D28): for every model type,
    /// two unrelated classes implementing <see cref="IPersistentObjectActions{T}"/> for a CLR entity that no
    /// registration settles, a class beside the entity named for it that does not implement the contract (F3),
    /// or a virtual type's <c>{Name}Actions</c> declared twice where it is looked up.
    /// Both used to surface only at the first request that resolved the type, because the startup checks
    /// that resolve actions classes swallow construction failures. Throws one
    /// <see cref="InvalidOperationException"/> naming every offender.
    /// </summary>
    /// <param name="isRegistered">Whether the container registers <see cref="IPersistentObjectActions{T}"/> for a CLR entity (a registration wins, so it settles an ambiguity).</param>
    internal static void VerifyActionsClasses(IEnumerable<EntityTypeDefinition> entityTypes, Func<Type, bool>? isRegistered = null)
    {
        var all = entityTypes.ToList();
        var problems = new List<string>();
        foreach (var type in all)
        {
            try
            {
                var clr = string.IsNullOrEmpty(type.ClrType) ? null : SparkTypeResolver.ResolveClrType(type.ClrType);
                if (clr is not null)
                {
                    if (isRegistered?.Invoke(clr) != true && FindActionsTypeFor(clr) is null)
                        RefuseMisnamedActionsClass(clr);
                }
                else if (string.IsNullOrEmpty(type.ClrType))
                {
                    FindVirtualActionsType(type, all);
                }
            }
            catch (InvalidOperationException ex)
            {
                problems.Add(ex.Message);
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Invalid actions classes: " + string.Join(Environment.NewLine, problems.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>Every class that could be an actions class: concrete, closed, not a private nested helper, not generated at run time (a mock).</summary>
    private static IEnumerable<Type> CandidateTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || assembly.GetName().Name is not { } name || SparkAssemblies.IsPlatform(name))
                continue;

            foreach (var type in TypesOf(assembly))
            {
                if (type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false, IsNestedPrivate: false })
                    yield return type;
            }
        }
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // The types that did load can still be actions classes.
            return ex.Types.OfType<Type>();
        }
    }

    /// <param name="assemblies">The assemblies to look in (<paramref name="include"/>) or to skip (otherwise).</param>
    private static Type? FindActionsType(string typeName, string[] assemblies, bool include)
    {
        // Negative caching matters here: when no Actions class exists for a virtual type, the
        // resolver still walks every loaded assembly on every request unless the null is cached.
        // ReflectionCache caches null too via Lazy<object?>.
        return ReflectionCache.GetOrAdd<Type?>(
            $"virtualActionsType|{typeName}|{(include ? "in" : "outside")}|{string.Join(",", assemblies)}",
            () =>
            {
                var scope = new HashSet<string>(assemblies, StringComparer.Ordinal);

                // F3. Collect every match rather than taking the first. Returning the first made
                // resolution depend on assembly enumeration order — which is not deterministic and
                // is not something an author controls — and the answer was then cached for the
                // process lifetime.
                var matches = new List<Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic || assembly.GetName().Name is not { } name) continue;
                    if (scope.Contains(name) != include) continue;
                    matches.AddRange(TypesOf(assembly).Where(t => t.Name == typeName && t is { IsAbstract: false, IsInterface: false }));
                }

                if (matches.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"'{typeName}' is declared by more than one loaded assembly, so the actions " +
                        $"class for this virtual type is ambiguous: " +
                        string.Join(", ", matches.Select(t => $"'{t.FullName}' ({t.Assembly.GetName().Name})")) +
                        ". Resolution used to pick whichever assembly loaded first and cache it for the " +
                        "process lifetime, which could silently substitute one type's actions class for " +
                        "another's. Rename all but one.");
                }

                return matches.Count == 1 ? matches[0] : null;
            });
    }
}
