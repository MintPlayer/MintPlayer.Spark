using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>A reserved <c>security.json</c> verb and the class that declared it.</summary>
/// <param name="Verb">The verb, as the constant spells it.</param>
/// <param name="DeclaringType">The class the assembly's <see cref="SparkReservedActionsAttribute"/> points at.</param>
/// <param name="Assembly">The assembly carrying that attribute (the verb's owner).</param>
public sealed record SparkReservedAction(string Verb, Type DeclaringType, Assembly Assembly);

/// <summary>
/// The reserved verbs, read by reflection from every <c>[assembly: SparkReservedActions(typeof(X))]</c>
/// (#460 contributions, Q2) — the runtime half of what the SPARK023 analyzer reads at build time.
/// </summary>
public static class SparkReservedActionRegistry
{
    private static readonly Lazy<IReadOnlyList<SparkReservedAction>> all = new(() => Discover(SparkAssemblies.SparkAware()));

    /// <summary>
    /// Every verb declared by an assembly loaded in this process or referenced by a Spark-aware one.
    /// References are followed because assemblies load lazily: a package the application references
    /// but has not touched yet (SoftDelete, before the first request) still owns its verbs.
    /// </summary>
    public static IReadOnlyList<SparkReservedAction> All => all.Value;

    /// <summary>Whether <paramref name="verb"/> is reserved (case-insensitive, like rights).</summary>
    public static bool IsReserved(string verb)
        => All.Any(a => string.Equals(a.Verb, verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>The verbs <paramref name="assemblies"/> declare.</summary>
    public static IReadOnlyList<SparkReservedAction> Discover(IEnumerable<Assembly> assemblies)
    {
        var result = new List<SparkReservedAction>();
        foreach (var assembly in assemblies.Distinct())
        {
            IEnumerable<SparkReservedActionsAttribute> declarations;
            try { declarations = assembly.GetCustomAttributes<SparkReservedActionsAttribute>(); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException) { continue; }

            foreach (var declaration in declarations)
                result.AddRange(VerbsOf(declaration.Verbs).Select(verb => new SparkReservedAction(verb, declaration.Verbs, assembly)));
        }
        return result;
    }

    /// <summary>The <c>public const string</c> fields of <paramref name="type"/>, minus <see cref="SparkNotAnActionAttribute"/> ones.</summary>
    internal static IEnumerable<string> VerbsOf(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && !f.IsDefined(typeof(SparkNotAnActionAttribute), false))
            .Select(f => (string?)f.GetRawConstantValue())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!);

    /// <summary>
    /// Refuses a custom action named like a reserved verb: it would share that verb's right
    /// (<c>Restore/Car</c> would authorize both the action and the restore), and the framework's own
    /// endpoint for the verb would shadow or be shadowed by it.
    /// </summary>
    /// <param name="customActions">Each custom action's name (its class name without the <c>Action</c> suffix) and class.</param>
    /// <param name="reserved">The reserved verbs, normally <see cref="All"/>.</param>
    /// <exception cref="InvalidOperationException">A name collides; the message names the action, the verb and the verb's owning assembly.</exception>
    public static void EnsureNoCollisions(IEnumerable<(string Name, Type Type)> customActions, IReadOnlyList<SparkReservedAction> reserved)
    {
        var problems = new List<string>();
        foreach (var (name, type) in customActions)
        {
            var collision = reserved.FirstOrDefault(r => string.Equals(r.Verb, name, StringComparison.OrdinalIgnoreCase));
            if (collision is not null)
                problems.Add(
                    $"Custom action '{name}' ({type.FullName}) is named like the reserved action verb '{collision.Verb}', "
                    + $"declared by {collision.DeclaringType.FullName} in assembly '{collision.Assembly.GetName().Name}'.");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, problems)
                + $"{Environment.NewLine}A reserved verb is a right Spark or a package asks for; an action of the same name would share it. Rename the action class.");
    }
}
