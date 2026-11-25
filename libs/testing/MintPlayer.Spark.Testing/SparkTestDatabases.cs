using System.Collections.Concurrent;
using System.Reflection;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// The database type a test assembly runs on (#536 O16): the one its <see cref="SparkTestDatabaseAttribute"/> names,
/// or, with several, the one <c>SPARK_TEST_DATABASE</c> names. It never falls back to a default: a SQL Server leg
/// that silently ran another database would pass.
/// </summary>
public static class SparkTestDatabases
{
    /// <summary>The environment variable that picks a provider when the assembly names several.</summary>
    public const string Variable = "SPARK_TEST_DATABASE";

    private static readonly ConcurrentDictionary<Assembly, Lazy<ISparkTestDatabaseProvider>> Providers = new();

    /// <summary>The provider of <paramref name="testAssembly"/>, created once per process.</summary>
    /// <exception cref="InvalidOperationException">None, an ambiguous or an unknown choice; the message names the candidates.</exception>
    public static ISparkTestDatabaseProvider For(Assembly testAssembly)
    {
        ArgumentNullException.ThrowIfNull(testAssembly);
        return Providers.GetOrAdd(testAssembly, assembly => new Lazy<ISparkTestDatabaseProvider>(() =>
            (ISparkTestDatabaseProvider)Activator.CreateInstance(Select(
                assembly.GetName().Name,
                assembly.GetCustomAttributes<SparkTestDatabaseAttribute>().Select(a => a.ProviderType),
                Environment.GetEnvironmentVariable(Variable)))!)).Value;
    }

    /// <summary>The rule, apart from the attributes and the environment.</summary>
    internal static Type Select(string? assemblyName, IEnumerable<Type> candidates, string? variable)
    {
        var types = candidates.Distinct().ToArray();
        foreach (var type in types)
            if (!typeof(ISparkTestDatabaseProvider).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException(
                    $"[SparkTestDatabase(typeof({type.FullName}))] names no ISparkTestDatabaseProvider with a public parameterless constructor.");

        var names = types.ToDictionary(NameOf, StringComparer.OrdinalIgnoreCase);
        var listed = string.Join(", ", names.Keys.Order(StringComparer.OrdinalIgnoreCase));

        if (types.Length == 0)
            throw new InvalidOperationException(
                $"Test assembly {assemblyName} names no database type. Reference a provider testing package "
                + "(MintPlayer.Spark.Testing.RavenDB, .SqlServer or .PostgreSql), or add "
                + "[assembly: SparkTestDatabase(typeof(…))].");

        if (!string.IsNullOrWhiteSpace(variable))
            return names.TryGetValue(variable.Trim(), out var chosen)
                ? chosen
                : throw new InvalidOperationException(
                    $"{Variable}={variable} names a database type test assembly {assemblyName} does not reference. Candidates: {listed}.");

        return types.Length == 1
            ? types[0]
            : throw new InvalidOperationException(
                $"Test assembly {assemblyName} references several database types ({listed}). Set {Variable} to one of them.");
    }

    /// <summary>The provider's <see cref="ISparkTestDatabaseProvider.Name"/>, read from an instance.</summary>
    private static string NameOf(Type type) => ((ISparkTestDatabaseProvider)Activator.CreateInstance(type)!).Name;
}
