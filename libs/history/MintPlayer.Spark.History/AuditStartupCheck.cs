using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.History;

/// <summary>
/// What <c>AddHistory()</c> checks when <c>UseSpark()</c> runs (#271): every model type implementing
/// <see cref="IAuditCreated"/> or <see cref="IAuditModified"/> declares those members as public
/// read/write instance properties of the interface's types. Refuses startup otherwise.
/// </summary>
/// <remarks>
/// RavenDB stores only public properties, so an explicit interface implementation compiles, is stamped
/// on every write, and is silently never stored. The generator (<c>MintPlayer.Spark.LibraryGenerators</c>)
/// never produces that shape; this catches the hand-written one, as <c>SoftDeleteStartupCheck</c> does
/// for <c>ISoftDeletable</c>.
/// </remarks>
internal static class AuditStartupCheck
{
    private static readonly (Type Contract, string Name, Type Type)[] Members =
    [
        (typeof(IAuditCreated), nameof(IAuditCreated.CreatedBy), typeof(string)),
        (typeof(IAuditCreated), nameof(IAuditCreated.CreatedAt), typeof(DateTimeOffset?)),
        (typeof(IAuditModified), nameof(IAuditModified.ModifiedBy), typeof(string)),
        (typeof(IAuditModified), nameof(IAuditModified.ModifiedAt), typeof(DateTimeOffset?)),
    ];

    public static void Run(IServiceProvider services)
    {
        var modelLoader = services.GetRequiredService<IModelLoader>();

        var problems = modelLoader.GetEntityTypes()
            .Select(definition => Resolve(definition.ClrType))
            .Where(type => type is not null)
            .Select(type => type!)
            .Distinct()
            .SelectMany(MemberProblems)
            .ToList();

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "History cannot stamp these audit members:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    internal static IEnumerable<string> MemberProblems(Type type)
    {
        foreach (var (contract, name, memberType) in Members)
        {
            if (!contract.IsAssignableFrom(type))
                continue;

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                yield return $"{type.FullName}.{name} is not a public property (an explicit interface implementation is stored nowhere).";
            else if (property.PropertyType != memberType)
                yield return $"{type.FullName}.{name} is {property.PropertyType.Name}, expected {memberType.Name}.";
            else if (!property.CanRead || !property.CanWrite || property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
                yield return $"{type.FullName}.{name} must have a public getter and setter.";
        }
    }

    private static readonly ConcurrentDictionary<string, Type?> Cache = new(StringComparer.Ordinal);

    private static Type? Resolve(string? clrType) => clrType is null ? null : Cache.GetOrAdd(clrType, static name =>
    {
        if (Type.GetType(name) is { } type)
            return type;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetType(name) is { } found)
                return found;
        return null;
    });
}
