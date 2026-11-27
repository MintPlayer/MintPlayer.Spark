using System.Reflection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Commands;

/// <summary>
/// The schema half of <c>--spark-synchronize-model</c> (#536 D24, O17): on a provider with a schema, after the model
/// JSON is written, the provider's design-time scaffolder writes the migration for the change. RavenDB and in-memory
/// register no <see cref="SparkSchemaDesignAssembly"/>, so nothing happens there.
/// </summary>
internal static class SparkSchemaScaffolding
{
    internal const string NameFlag = "--name";

    internal static async Task<int> RunAsync(IServiceProvider services, IHostEnvironment environment, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (services.GetService<SparkSchemaDesignAssembly>() is not { } design)
            return 0;

        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(design.AssemblyName));
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine(
                $"The model JSON is written, but no migration: the store keeps a schema, and {design.AssemblyName} is not in the "
                + $"app's bin. Reference the {design.AssemblyName} package in the host project (a development dependency: it stays "
                + "out of every publish), then run --spark-synchronize-model again.");
            return 1;
        }

        if (assembly.GetCustomAttribute<SparkSchemaScaffolderAttribute>() is not { } attribute
            || Activator.CreateInstance(attribute.ScaffolderType) is not ISparkSchemaScaffolder scaffolder)
        {
            Console.Error.WriteLine($"{design.AssemblyName} names no ISparkSchemaScaffolder ([assembly: SparkSchemaScaffolder]).");
            return 1;
        }

        var name = args.SkipWhile(a => a != NameFlag).Skip(1).FirstOrDefault();
        var result = await scaffolder.ScaffoldAsync(services, new SparkScaffoldRequest(environment.ContentRootPath, name), cancellationToken);
        if (result.Files.Count == 0)
        {
            Console.WriteLine("Schema: no model change, no migration written.");
            return 0;
        }

        Console.WriteLine("Schema: a migration for the model change. Review it, and commit it with the model:");
        foreach (var file in result.Files)
            Console.WriteLine("  " + Path.GetRelativePath(environment.ContentRootPath, file));
        foreach (var operation in result.Operations)
            Console.WriteLine("  - " + operation);
        foreach (var warning in result.Warnings)
            Console.WriteLine("  warning: " + warning);
        return 0;
    }
}
