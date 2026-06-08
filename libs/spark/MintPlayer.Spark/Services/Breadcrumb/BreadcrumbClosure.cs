using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Collections.Concurrent;

namespace MintPlayer.Spark.Services.Breadcrumb;

/// <summary>
/// An immediate breadcrumb reference edge: a <c>{Attribute}</c> placeholder in an entity's
/// breadcrumb that points at another entity (so rendering it recurses into the target's breadcrumb).
/// </summary>
public sealed record BreadcrumbReference(string AttributeName, string TargetClrType, bool IsArray);

/// <summary>
/// Static analysis of the breadcrumb reference graph, derived purely from the model
/// (<see cref="IModelLoader"/>) — no database access. Provides the per-type reference edges that
/// drive the resolver's breadth-first loading, plus depth and cycle diagnostics.
/// </summary>
internal interface IBreadcrumbClosure
{
    /// <summary>The reference placeholders in <paramref name="def"/>'s breadcrumb (cached).</summary>
    IReadOnlyList<BreadcrumbReference> GetReferences(EntityTypeDefinition def);

    /// <summary>
    /// Maximum breadcrumb nesting reachable from <paramref name="def"/>: 1 = scalar-only,
    /// 2 = one reference hop, etc. Cycles terminate (the cyclic edge contributes 0), so this
    /// is always finite.
    /// </summary>
    int GetDepth(EntityTypeDefinition def);

    /// <summary>
    /// Cycles in the breadcrumb reference graph (each a list of CLR type names forming the loop).
    /// Cycles are not fatal — the resolver bounds recursion by depth and a per-path visited set —
    /// but they are surfaced so a developer can spot an unintended self-reference.
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> GetCycles();
}

[Register(typeof(IBreadcrumbClosure), ServiceLifetime.Singleton)]
internal partial class BreadcrumbClosure : IBreadcrumbClosure
{
    [Inject] private readonly IModelLoader modelLoader;

    private readonly ConcurrentDictionary<string, IReadOnlyList<BreadcrumbReference>> _refsByClrType = new(StringComparer.Ordinal);
    private readonly object _cycleLock = new();
    private IReadOnlyList<IReadOnlyList<string>>? _cycles;

    public IReadOnlyList<BreadcrumbReference> GetReferences(EntityTypeDefinition def)
        => _refsByClrType.GetOrAdd(def.ClrType, _ => ComputeReferences(def));

    private static IReadOnlyList<BreadcrumbReference> ComputeReferences(EntityTypeDefinition def)
    {
        if (string.IsNullOrEmpty(def.Breadcrumb))
            return [];

        var attrByName = def.Attributes.ToDictionary(a => a.Name, a => a, StringComparer.Ordinal);
        var refs = new List<BreadcrumbReference>();

        // FieldNames is distinct, so a reference used twice in the template is followed once.
        foreach (var name in BreadcrumbTemplate.FieldNames(def.Breadcrumb))
        {
            // Unknown placeholders are already rejected at model-sync time; stay lenient here.
            if (!attrByName.TryGetValue(name, out var attr))
                continue;
            if (attr.DataType == "Reference" && !string.IsNullOrEmpty(attr.ReferenceType))
                refs.Add(new BreadcrumbReference(name, attr.ReferenceType!, attr.IsArray));
        }

        return refs;
    }

    public int GetDepth(EntityTypeDefinition def)
        => ComputeDepth(def, []);

    private int ComputeDepth(EntityTypeDefinition def, HashSet<string> path)
    {
        if (!path.Add(def.ClrType))
            return 0; // cycle — stop descending, the cyclic edge adds no further depth

        var maxChild = 0;
        foreach (var reference in GetReferences(def))
        {
            var target = modelLoader.GetEntityTypeByClrType(reference.TargetClrType);
            if (target is null)
                continue;
            maxChild = Math.Max(maxChild, ComputeDepth(target, path));
        }

        path.Remove(def.ClrType);
        return 1 + maxChild;
    }

    public IReadOnlyList<IReadOnlyList<string>> GetCycles()
    {
        if (_cycles is not null)
            return _cycles;
        lock (_cycleLock)
            return _cycles ??= DetectCycles();
    }

    private IReadOnlyList<IReadOnlyList<string>> DetectCycles()
    {
        var cycles = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in modelLoader.GetEntityTypes())
            Dfs(def, [], [], cycles, seen);

        return cycles;
    }

    private void Dfs(
        EntityTypeDefinition def,
        List<string> path,
        HashSet<string> inPath,
        List<IReadOnlyList<string>> cycles,
        HashSet<string> seen)
    {
        if (inPath.Contains(def.ClrType))
        {
            var start = path.IndexOf(def.ClrType);
            var cycle = path.Skip(start).Append(def.ClrType).ToList();
            // Dedupe rotations/re-discoveries: key by the unordered set of nodes in the loop.
            var key = string.Join("|", cycle.Take(cycle.Count - 1).OrderBy(x => x, StringComparer.Ordinal));
            if (seen.Add(key))
                cycles.Add(cycle);
            return;
        }

        path.Add(def.ClrType);
        inPath.Add(def.ClrType);

        foreach (var reference in GetReferences(def))
        {
            var target = modelLoader.GetEntityTypeByClrType(reference.TargetClrType);
            if (target is not null)
                Dfs(target, path, inPath, cycles, seen);
        }

        path.RemoveAt(path.Count - 1);
        inPath.Remove(def.ClrType);
    }
}
