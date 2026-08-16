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

    /// <summary>Exit code for a Spark misconfiguration that prevented the command from running.</summary>
    private const int ExitMisconfigured = 2;

    /// <summary>
    /// Runs model synchronization if <c>--spark-synchronize-model</c> is present in
    /// <paramref name="args"/>, and reports whether the host should stop instead of starting.
    /// <para>
    /// The entity context type is taken from the registration made by
    /// <c>spark.UseContext&lt;TContext&gt;()</c>, so no type argument is needed here.
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

        if (!args.Contains(SynchronizeFlag))
            return false;

        if (!TryCreateRegisteredContext(builder.Services, out var sparkContext))
            return true;

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

        if (!args.Contains(SynchronizeFlag))
            return false;

        Synchronize(builder, new TContext());
        return true;
    }

    private static void Synchronize(WebApplicationBuilder builder, SparkContext sparkContext)
    {
        // Session stays null on purpose. The synchronizer reflects over the context's property
        // TYPES and never invokes a getter, so no RavenDB connection is opened — which is what
        // makes this runnable in CI. Opening one here would reintroduce that dependency.
        var indexRegistry = new IndexRegistry();
        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly is null)
        {
            Console.Error.WriteLine("Spark: could not determine the entry assembly, so index and projection types cannot be discovered.");
            Environment.ExitCode = ExitMisconfigured;
            return;
        }

        SparkExtensions.PopulateIndexRegistry(indexRegistry, entryAssembly);

        var synchronizer = new ModelSynchronizer(builder.Environment, indexRegistry);
        synchronizer.SynchronizeModels(sparkContext);

        Console.WriteLine("Model synchronization completed.");
    }

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
