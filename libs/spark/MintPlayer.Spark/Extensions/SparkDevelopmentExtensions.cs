using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using System.Reflection;

namespace MintPlayer.Spark;

/// <summary>
/// Build-time Spark commands, invoked from <c>Program.cs</c> before <c>builder.Build()</c>.
/// <para>
/// Model synchronization is a code-generation step, not a run mode: it reflects over the entity
/// classes and writes <c>App_Data/Model/*.json</c>. It needs no database, no service provider and no
/// middleware pipeline, so it runs in the builder phase and the host simply returns from
/// <c>Main</c>. That is what lets it run in a CI merge queue, where no RavenDB exists.
/// </para>
/// </summary>
public static class SparkDevelopmentExtensions
{
    internal const string SynchronizeFlag = "--spark-synchronize-model";
    internal const string VerifyFlag = "--spark-verify-model";

    /// <summary>Exit code for a Spark misconfiguration that prevented the command from running.</summary>
    private const int ExitMisconfigured = 2;

    /// <summary>Exit code for a model that is out of sync, reported by <c>--spark-verify-model</c>.</summary>
    private const int ExitDrift = 3;

    /// <summary>
    /// Handles the build-time model commands and reports whether the host should stop instead of
    /// starting.
    /// <list type="bullet">
    /// <item><c>--spark-synchronize-model</c> regenerates <c>App_Data/Model</c> and the hash file.</item>
    /// <item><c>--spark-verify-model</c> writes nothing and exits 3 if the model has drifted — the
    /// merge-queue gate.</item>
    /// </list>
    /// <para>
    /// The entity context type is taken from the registration made by
    /// <c>spark.UseContext&lt;TContext&gt;()</c>, so no type argument is needed here.
    /// </para>
    /// <para>
    /// Neither command opens a database connection, so both run in CI.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a command was handled and the host should return from
    /// <c>Main</c>; <see langword="false"/> when no Spark command was requested.
    /// </returns>
    /// <example>
    /// <code>
    /// builder.Services.AddSpark(builder.Configuration, spark => spark.UseContext&lt;MyContext&gt;());
    ///
    /// if (builder.SynchronizeSparkModelsIfRequested(args))
    ///     return;
    /// </code>
    /// </example>
    public static bool SynchronizeSparkModelsIfRequested(this WebApplicationBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        var verifyOnly = args.Contains(VerifyFlag);
        if (!verifyOnly && !args.Contains(SynchronizeFlag))
            return false;

        if (!TryResolveRegisteredContextType(builder.Services, out var contextType))
            return true;

        if (verifyOnly)
            Verify(builder, contextType);
        else
            Synchronize(builder, contextType);

        return true;
    }

    /// <summary>
    /// Explicit-context overload of
    /// <see cref="SynchronizeSparkModelsIfRequested(WebApplicationBuilder, string[])"/>, for apps that
    /// prefer to name the context at the call site rather than rely on the registration.
    /// </summary>
    public static bool SynchronizeSparkModelsIfRequested<TContext>(this WebApplicationBuilder builder, string[] args)
        where TContext : SparkContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        var verifyOnly = args.Contains(VerifyFlag);
        if (!verifyOnly && !args.Contains(SynchronizeFlag))
            return false;

        if (verifyOnly)
            Verify(builder, typeof(TContext));
        else
            Synchronize(builder, typeof(TContext));

        return true;
    }

    /// <summary>
    /// Writes <c>App_Data/modelHashes.json</c> for an already-populated model directory, without
    /// regenerating the model files.
    /// <para>
    /// For hosts that author model files directly rather than through synchronization — chiefly test
    /// hosts, which stand up an application from fixture models. Without a matching hash file such a
    /// host would fail the startup check, which fails closed by design.
    /// </para>
    /// <para>
    /// Uses the same index catalog source as the startup check, so the value written and the value
    /// verified cannot diverge.
    /// </para>
    /// </summary>
    public static void WriteSparkModelHashes(Type contextType, string contentRootPath)
        => WriteSparkModelHashes(contextType, contentRootPath, services: null);

    /// <summary>
    /// As <see cref="WriteSparkModelHashes(Type, string)"/>, but resolving index assemblies from
    /// <paramref name="services"/> so any declared by a module are included.
    /// <para>
    /// Call this only once <c>AddSpark</c> has returned. Called earlier, the declarations have not
    /// happened yet and the value written would disagree with the value the startup check computes.
    /// </para>
    /// </summary>
    public static void WriteSparkModelHashes(Type contextType, string contentRootPath, IServiceCollection? services)
        => WriteSparkModelHashes(contextType, contentRootPath, services, configureIndexCatalog: null);

    /// <summary>
    /// As <see cref="WriteSparkModelHashes(Type, string, IServiceCollection?)"/>, additionally applying
    /// <paramref name="configureIndexCatalog"/> to the offline catalog before it freezes. A test host
    /// that arms fixture indexes into the runtime catalog must arm the hash writer's catalog with the
    /// same hook, or the value written and the value the startup check computes describe different
    /// models and the host refuses to start.
    /// </summary>
    public static void WriteSparkModelHashes(
        Type contextType,
        string contentRootPath,
        IServiceCollection? services,
        Action<IIndexCatalog>? configureIndexCatalog)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        ArgumentNullException.ThrowIfNull(contentRootPath);

        var indexCatalog = new IndexCatalog();

        var assemblies = (services is null ? null : GetRegistrationTimeModuleRegistry(services)?.ResolveIndexAssemblies())
            ?? (Assembly.GetEntryAssembly() is { } entryAssembly ? [entryAssembly] : (IReadOnlyList<Assembly>)[]);

        foreach (var assembly in assemblies)
            SparkExtensions.PopulateIndexTypes(indexCatalog, assembly);

        foreach (var assembly in assemblies)
            SparkExtensions.PopulateProjectionTypes(indexCatalog, assembly);

        configureIndexCatalog?.Invoke(indexCatalog);

        indexCatalog.Freeze();

        ModelSynchronizer.BuildModelHashes(contextType, indexCatalog, contentRootPath)
            .Write(contentRootPath);
    }

    /// <summary>
    /// Reports whether the committed model still matches the entity classes, writing nothing.
    /// <para>
    /// This is the merge-queue gate: it answers "did this change touch the entities without
    /// regenerating the model?" without mutating the workspace, so later steps still see the tree as
    /// the pull request left it. Regenerating and diffing would work too, but it dirties the
    /// checkout, needs a git index, and reports drift for any unrelated dirt.
    /// </para>
    /// </summary>
    private static void Verify(WebApplicationBuilder builder, Type contextType)
    {
        if (!IsUsableContextType(contextType))
            return;

        if (!TryBuildIndexCatalog(builder.Services, out var indexCatalog))
            return;

        var contentRoot = builder.Environment.ContentRootPath;
        var expected = ModelHashFile.Read(contentRoot);
        var actual = ModelSynchronizer.BuildModelHashes(contextType, indexCatalog, contentRoot);

        // These two are independent of the hash and run either way (#327 M3). They used to sit
        // inside the in-sync branch below, so a change that both drifted the hash AND collided an
        // alias reported only the drift — and the collision stayed invisible until the drift was
        // fixed and CI was run again. Two round trips for one commit's worth of problems.
        VerifyQueryAliasesAreUnique(contentRoot);
        VerifyRefreshTriggersAreImplemented(contentRoot);

        if (expected is not null && string.Equals(expected.ModelHash, actual.ModelHash, StringComparison.Ordinal))
        {
            Console.WriteLine($"Spark model is in sync ({actual.ModelHash}).");
            return;
        }

        if (expected is null)
        {
            Console.Error.WriteLine($"Spark model is unverifiable: {ModelHashFile.PathFor(contentRoot)} is missing or unreadable.");
        }
        else
        {
            Console.Error.WriteLine("Spark model is out of sync.");
            Console.Error.WriteLine($"  expected {expected.ModelHash}");
            Console.Error.WriteLine($"  actual   {actual.ModelHash}");
            Console.Error.WriteLine();

            // Name what moved. A merge queue that only prints two hashes leaves the author guessing;
            // this is the difference between a one-line fix and a bisect.
            foreach (var line in ModelHashVerifier.DescribeDrift(expected, actual))
                Console.Error.WriteLine("  " + line);
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Run '{SynchronizeFlag}' and commit the regenerated App_Data/Model and {ModelHashFile.FileName}.");
        // A JSON-only virtual type (no clrType) has no CLR class to regenerate from, so synchronize
        // only re-stamps its hash. Same command, but "regenerated" would send the author looking for
        // a class that does not exist — which is the whole point of the type.
        Console.Error.WriteLine(
            $"  For a hand-authored model file (a JSON-only type with no clrType), nothing is regenerated — " +
            $"{SynchronizeFlag} just re-stamps {ModelHashFile.FileName} with what is on disk. Review the drift " +
            $"above first; that file is the source of truth for its own shape.");

        Environment.ExitCode = ExitDrift;
    }

    /// <summary>
    /// Refuses a model declaring <c>triggersRefresh</c> on a type whose actions class has no
    /// <c>OnRefreshAsync</c> override.
    /// </summary>
    /// <remarks>
    /// The flag is a promise to the user that changing this field does something. Unimplemented, it
    /// buys them a round trip per edit and no visible effect — a defect that is invisible in review,
    /// because the declaration and the implementation live in different files and different
    /// languages.
    /// <para>
    /// ⚠️ This cannot be a Roslyn analyzer, which is the obvious place to look for it. The flag
    /// lives in <c>App_Data/Model/*.json</c>, which is not part of the compilation unless it is
    /// added as an <c>AdditionalFile</c>; an analyzer would have nothing to read. It rides
    /// <c>--spark-verify-model</c> instead, which already runs in CI and already exits non-zero.
    /// </para>
    /// <para>
    /// Reads the files directly and resolves the actions type by the same convention
    /// <c>ActionsResolver</c> uses, because there is no service provider in the builder phase.
    /// </para>
    /// </remarks>
    private static void VerifyRefreshTriggersAreImplemented(string contentRootPath)
    {
        var modelPath = Path.Combine(contentRootPath, "App_Data", "Model");
        if (!Directory.Exists(modelPath))
            return;

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(modelPath, "*.json"))
        {
            EntityTypeFile? model;
            try
            {
                model = System.Text.Json.JsonSerializer.Deserialize<EntityTypeFile>(
                    File.ReadAllText(file),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed model files are the hash check's business, not this one's.
                continue;
            }

            var definition = model?.PersistentObject;
            if (definition is null)
                continue;

            var triggers = definition.Attributes
                .Where(a => a.TriggersRefresh == true)
                .Select(a => a.Name)
                .ToArray();

            if (triggers.Length == 0)
                continue;

            var entityName = definition.ClrType?.Split('.').Last() ?? definition.Name;
            if (HasRefreshOverride(entityName))
                continue;

            offenders.Add($"{definition.Name}: {string.Join(", ", triggers)} " +
                          $"(no OnRefreshAsync override on {entityName}Actions)");
        }

        if (offenders.Count == 0)
            return;

        Console.Error.WriteLine("Spark model declares refresh triggers that nothing implements:");
        foreach (var offender in offenders)
            Console.Error.WriteLine("  " + offender);
        Console.Error.WriteLine();
        Console.Error.WriteLine("Override OnRefreshAsync on the entity's actions class, or remove \"triggersRefresh\" from the model.");

        Environment.ExitCode = ExitDrift;
    }

    private static bool HasRefreshOverride(string entityName)
    {
        var actionsTypeName = entityName + "Actions";

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = [.. e.Types.Where(t => t is not null)!]; }

            foreach (var type in types)
            {
                if (type.IsAbstract || !string.Equals(type.Name, actionsTypeName, StringComparison.Ordinal))
                    continue;

                var method = type.GetMethod("OnRefreshAsync");
                var declaring = method?.DeclaringType;
                if (declaring is null)
                    continue;

                var isBaseDeclaration = declaring.IsGenericType
                    && declaring.GetGenericTypeDefinition() == typeof(Actions.DefaultPersistentObjectActions<>);

                if (!isBaseDeclaration)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Refuses a model whose query aliases collide, from the files on disk.
    /// </summary>
    /// <remarks>
    /// Part of <c>--spark-verify-model</c> rather than a check of its own, because it is the same
    /// question that command already answers — <em>is the committed model usable?</em> — and one
    /// more CI step is one more thing to forget to wire up.
    /// <para>
    /// It has to be checked here at all because the model commands return before
    /// <c>builder.Build()</c>, so <c>UseSpark</c>'s startup gate never runs in CI. A collision
    /// would otherwise only ever surface by running the application, which is exactly how DemoApp
    /// shipped one.
    /// </para>
    /// <para>
    /// Reads the files directly rather than resolving <c>IQueryLoader</c>: there is no service
    /// provider in the builder phase, and building one would need a document store. The rule
    /// itself is shared (<see cref="SparkQueryAliases.Index"/>), so only the reading differs.
    /// </para>
    /// </remarks>
    private static void VerifyQueryAliasesAreUnique(string contentRootPath)
    {
        var modelPath = Path.Combine(contentRootPath, "App_Data", "Model");
        if (!Directory.Exists(modelPath))
            return;

        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var queries = new List<SparkQuery>();

        foreach (var file in Directory.GetFiles(modelPath, "*.json"))
        {
            try
            {
                var entityTypeFile = System.Text.Json.JsonSerializer.Deserialize<EntityTypeFile>(
                    File.ReadAllText(file), jsonOptions);

                if (entityTypeFile?.Queries is { Length: > 0 } fileQueries)
                    queries.AddRange(fileQueries);
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed model file is the hash check's problem, not this one. Reporting it
                // twice, differently, helps nobody.
            }
        }

        try
        {
            SparkQueryAliases.Index(queries);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = ExitDrift;
        }
    }

    private static void Synchronize(WebApplicationBuilder builder, Type contextType)
    {
        // The context is never instantiated. The synchronizer reflects over the context's property
        // TYPES and never invokes a getter, so no session, no service provider and no RavenDB
        // connection are needed — which is what makes this runnable in CI, and what lets an
        // application put constructor dependencies on its context (#292).
        if (!IsUsableContextType(contextType))
            return;

        if (!TryBuildIndexCatalog(builder.Services, out var indexCatalog))
            return;

        var synchronizer = new ModelSynchronizer(builder.Environment, indexCatalog);
        synchronizer.SynchronizeModels(contextType);

        Console.WriteLine("Model synchronization completed.");
    }

    /// <summary>
    /// Builds a frozen index catalog from the declared assemblies. Shared by both commands so a
    /// synchronize and the verify that checks it can never see a different set of projections.
    /// <para>
    /// Reads the declarations off the module registry's singleton descriptor — the same
    /// registration-time trick used for the host environment and the context type — so the build-time
    /// commands and the running application resolve the same assemblies. Falls back to the entry
    /// assembly when no host called <c>AddSpark</c>.
    /// </para>
    /// <para>
    /// Freezing runs the <c>[DefaultIndex]</c> validation, so an ambiguous default fails the command
    /// here exactly as it would fail the application at startup.
    /// </para>
    /// </summary>
    private static bool TryBuildIndexCatalog(IServiceCollection services, out IIndexCatalog indexCatalog)
    {
        var catalog = new IndexCatalog();
        indexCatalog = catalog;

        var assemblies = GetRegistrationTimeModuleRegistry(services)?.ResolveIndexAssemblies()
            ?? (Assembly.GetEntryAssembly() is { } entryAssembly ? [entryAssembly] : (IReadOnlyList<Assembly>)[]);

        if (assemblies.Count == 0)
        {
            Console.Error.WriteLine(
                "Spark: could not determine the entry assembly, so index and projection types cannot be discovered.");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        // Indexes across every assembly before any projection: a projection resolves its index by
        // name, so cross-assembly projections must not depend on scan order.
        foreach (var assembly in assemblies)
            SparkExtensions.PopulateIndexTypes(catalog, assembly);

        foreach (var assembly in assemblies)
            SparkExtensions.PopulateProjectionTypes(catalog, assembly);

        try
        {
            catalog.Freeze();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Spark: {ex.Message}");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the module registry at <em>registration</em> time. It is registered as a singleton
    /// instance, so it can be read straight off the descriptor with no container.
    /// </summary>
    private static SparkModuleRegistry? GetRegistrationTimeModuleRegistry(IServiceCollection services)
        => services.LastOrDefault(d => d.ServiceType == typeof(SparkModuleRegistry))?.ImplementationInstance
            as SparkModuleRegistry;

    /// <summary>
    /// Recovers the concrete <see cref="SparkContext"/> type from the registration made by
    /// <c>UseContext&lt;TContext&gt;()</c>. Reports the specific misconfiguration rather than letting
    /// a null reference surface later.
    /// </summary>
    /// <remarks>
    /// The type is never instantiated. Both commands read property <em>types</em> only, so an
    /// instance would carry nothing but its own <c>GetType()</c> — and requiring one imposed a public
    /// parameterless constructor on every consuming context, which ruled out putting any dependency
    /// on it (#292). Note the asymmetry that made this a papercut rather than a decision:
    /// <c>UseContext&lt;TContext&gt;</c> never had a <c>new()</c> constraint, so the compiler accepted
    /// such a context and only these commands rejected it.
    /// </remarks>
    private static bool TryResolveRegisteredContextType(IServiceCollection services, out Type contextType)
    {
        contextType = null!;

        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(SparkContext));
        if (descriptor is null)
        {
            Console.Error.WriteLine(
                $"Spark: {SynchronizeFlag} was passed but no SparkContext is registered. " +
                "Call spark.UseContext<TContext>() inside AddSpark(...), or use the " +
                "SynchronizeSparkModelsIfRequested<TContext>(args) overload.");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        if (descriptor.ImplementationType is not { } registeredType)
        {
            Console.Error.WriteLine(
                "Spark: the registered SparkContext has no implementation type, so its model shape " +
                "cannot be determined. Use the " +
                "SynchronizeSparkModelsIfRequested<TContext>(args) overload.");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        contextType = registeredType;
        return true;
    }

    /// <summary>
    /// Rejects a context type that cannot describe a model: the abstract <see cref="SparkContext"/>
    /// base itself, or any abstract subclass.
    /// </summary>
    /// <remarks>
    /// This guard used to be accidental. While the commands instantiated the context,
    /// <c>Activator.CreateInstance</c> threw on an abstract type and the mistake could not happen;
    /// resolving a <see cref="Type"/> instead removes that barrier, so the check has to be deliberate.
    /// <para>
    /// It matters because the failure is silent and lands far from its cause. The property scan looks
    /// for <c>IRavenQueryable&lt;&gt;</c> properties, and the base type has none — so the model shape
    /// comes back empty, and while no model file is deleted, <c>modelHashes.json</c> is rewritten to
    /// certify an empty model over a still-populated model directory. <c>--spark-verify-model</c>
    /// cannot catch it, because it derives both sides of its comparison from the same caller-supplied
    /// type; the running application uses the DI instance's concrete type, so the mismatch first
    /// appears as a startup failure in Production.
    /// </para>
    /// </remarks>
    private static bool IsUsableContextType(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);

        if (contextType == typeof(SparkContext) || contextType.IsAbstract)
        {
            Console.Error.WriteLine(
                $"Spark: '{contextType.Name}' is not a concrete SparkContext, so it declares no query " +
                "roots and would describe an empty model. Pass the application's own context type " +
                "(the one registered with spark.UseContext<TContext>()).");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        if (!typeof(SparkContext).IsAssignableFrom(contextType))
        {
            Console.Error.WriteLine(
                $"Spark: '{contextType.Name}' does not derive from SparkContext.");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        return true;
    }
}
