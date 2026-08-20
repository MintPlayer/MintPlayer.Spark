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

        if (!TryCreateRegisteredContext(builder.Services, out var sparkContext))
            return true;

        if (verifyOnly)
            Verify(builder, sparkContext);
        else
            Synchronize(builder, sparkContext);

        return true;
    }

    /// <summary>
    /// Explicit-context overload of
    /// <see cref="SynchronizeSparkModelsIfRequested(WebApplicationBuilder, string[])"/>, for apps that
    /// prefer to name the context at the call site rather than rely on the registration.
    /// </summary>
    public static bool SynchronizeSparkModelsIfRequested<TContext>(this WebApplicationBuilder builder, string[] args)
        where TContext : SparkContext, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        var verifyOnly = args.Contains(VerifyFlag);
        if (!verifyOnly && !args.Contains(SynchronizeFlag))
            return false;

        if (verifyOnly)
            Verify(builder, new TContext());
        else
            Synchronize(builder, new TContext());

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
    private static void Verify(WebApplicationBuilder builder, SparkContext sparkContext)
    {
        if (!TryBuildIndexCatalog(builder.Services, out var indexCatalog))
            return;

        var contentRoot = builder.Environment.ContentRootPath;
        var expected = ModelHashFile.Read(contentRoot);
        var actual = ModelSynchronizer.BuildModelHashes(sparkContext.GetType(), indexCatalog, contentRoot);

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

        Environment.ExitCode = ExitDrift;
    }

    private static void Synchronize(WebApplicationBuilder builder, SparkContext sparkContext)
    {
        // Session stays null on purpose. The synchronizer reflects over the context's property
        // TYPES and never invokes a getter, so no RavenDB connection is opened — which is what
        // makes this runnable in CI. Opening one here would reintroduce that dependency.
        if (!TryBuildIndexCatalog(builder.Services, out var indexCatalog))
            return;

        var synchronizer = new ModelSynchronizer(builder.Environment, indexCatalog);
        synchronizer.SynchronizeModels(sparkContext);

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
    /// <c>UseContext&lt;TContext&gt;()</c> and instantiates it. Reports the specific
    /// misconfiguration rather than letting a null reference surface later.
    /// </summary>
    private static bool TryCreateRegisteredContext(IServiceCollection services, out SparkContext sparkContext)
    {
        sparkContext = null!;

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

        if (descriptor.ImplementationType is not { } contextType)
        {
            Console.Error.WriteLine(
                "Spark: the registered SparkContext has no implementation type, so it cannot be " +
                "constructed for model synchronization. Use the " +
                "SynchronizeSparkModelsIfRequested<TContext>(args) overload.");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        if (contextType.GetConstructor(Type.EmptyTypes) is null)
        {
            Console.Error.WriteLine(
                $"Spark: SparkContext '{contextType.Name}' has no public parameterless constructor, " +
                "which model synchronization requires in order to construct it without a service provider.");
            Environment.ExitCode = ExitMisconfigured;
            return false;
        }

        sparkContext = (SparkContext)Activator.CreateInstance(contextType)!;
        return true;
    }
}
