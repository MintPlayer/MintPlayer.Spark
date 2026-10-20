using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Contracts;

namespace MintPlayer.Spark;

/// <summary>
/// The application's TypeScript contracts commands (commerce X20 item 2), run from
/// <c>SynchronizeSparkModelsIfRequested</c> like the model commands: no database, no service provider.
/// <list type="bullet">
/// <item><c>--spark-synchronize-contracts</c> writes <c>ClientApp/src/app/generated/contracts.ts</c> from the
/// application's <see cref="SparkContractAttribute"/> types.</item>
/// <item><c>--spark-verify-contracts</c> writes nothing and exits 3 when the file differs: the CI gate.</item>
/// </list>
/// An application that claims no namespace (<see cref="SparkClientModuleAttribute"/>) has no contracts; both
/// commands then succeed without touching anything.
/// </summary>
public static class SparkContractCommands
{
    internal const string SynchronizeFlag = "--spark-synchronize-contracts";
    internal const string VerifyFlag = "--spark-verify-contracts";

    private const int ExitMisconfigured = 2;
    private const int ExitDrift = 3;

    /// <summary>Runs a contracts command when <paramref name="args"/> asks for one; true when the host should stop.</summary>
    internal static bool RunIfRequested(WebApplicationBuilder builder, string[] args)
    {
        var verifyOnly = args.Contains(VerifyFlag);
        if (!verifyOnly && !args.Contains(SynchronizeFlag))
            return false;

        if (Assembly.GetEntryAssembly() is not { } application)
        {
            Console.Error.WriteLine("The application's assembly is unknown (no entry assembly), so its contracts cannot be generated.");
            Environment.ExitCode = ExitMisconfigured;
            return true;
        }

        Environment.ExitCode = Run(application, builder.Environment.ContentRootPath, verifyOnly, Console.Out, Console.Error);
        return true;
    }

    /// <summary>
    /// Generates <paramref name="application"/>'s contracts into <paramref name="contentRootPath"/>, or checks them;
    /// returns the exit code (0, 2 for a contract that cannot be stated, 3 for drift).
    /// </summary>
    public static int Run(Assembly application, string contentRootPath, bool verifyOnly, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(contentRootPath);

        IReadOnlyList<SparkContractModule> modules;
        try
        {
            modules = SparkContractGenerator.Generate([application], ReferencedAssemblies(application), "dotnet run -- " + SynchronizeFlag);
        }
        catch (SparkContractException ex)
        {
            error.WriteLine(ex.Message);
            return ExitMisconfigured;
        }

        if (modules.FirstOrDefault(m => m.Module != SparkClientModuleAttribute.Application) is { } library)
        {
            error.WriteLine($"An application's contracts go to the '{SparkClientModuleAttribute.Application}' module; '{library.Module}' is a library's. Claim the namespace with [assembly: SparkClientModule(\"...\", SparkClientModuleAttribute.Application)].");
            return ExitMisconfigured;
        }

        var drifted = SparkContractGenerator.Synchronize(modules, _ => SparkContractGenerator.ApplicationModulePath(contentRootPath), verifyOnly);
        if (drifted.Count == 0)
        {
            output.WriteLine(modules.Count == 0 ? "No contracts: the application claims no namespace." : "The TypeScript contracts are up to date.");
            return 0;
        }

        foreach (var path in drifted)
            (verifyOnly ? error : output).WriteLine(verifyOnly ? $"Out of date: {path}" : $"Written: {path}");
        if (!verifyOnly)
            return 0;

        error.WriteLine($"Regenerate with: dotnet run -- {SynchronizeFlag}");
        return ExitDrift;
    }

    /// <summary>
    /// Every assembly <paramref name="root"/> references, transitively, except the framework's: where the libraries'
    /// claims and hand-written types are read from.
    /// </summary>
    public static IReadOnlyList<Assembly> ReferencedAssemblies(Assembly root)
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([root]);
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                if (name.Name is not { } simple || seen.ContainsKey(simple)
                    || simple.StartsWith("System", StringComparison.Ordinal) || simple.StartsWith("Microsoft", StringComparison.Ordinal)
                    || simple is "netstandard" or "mscorlib")
                    continue;
                try
                {
                    var loaded = Assembly.Load(name);
                    seen[simple] = loaded;
                    queue.Enqueue(loaded);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // A reference that is not deployed declares no contracts this application uses.
                }
            }
        }
        return [.. seen.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal)];
    }
}
