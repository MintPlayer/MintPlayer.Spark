using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// One replica class as its generated constants state it (warehouse §4.2.1, WD14), plus the owner modules it reads
/// from: the attribute's <c>SourceModule</c>, else <c>Spark:Replication:Sources:{TargetCollection}</c>.
/// </summary>
internal sealed record ReplicaDescriptor(
    Type Type,
    string SourceCollection,
    string TargetCollection,
    bool WriteBack,
    string Manifest,
    string Script,
    string Hash,
    ReplicaManifest Parsed,
    IReadOnlyList<string> Sources)
{
    /// <summary>Whether <paramref name="module"/> is a source of this replica.</summary>
    public bool IsSource(string module) => Sources.Contains(module, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The replica property → owner property map write-back can use: fields read from a top-level source member. A
    /// field read from a nested path cannot be written back.
    /// </summary>
    public IReadOnlyDictionary<string, string> WritableFields { get; } = Parsed.Fields
        .Where(f => f.Path.Count == 1)
        .ToDictionary(f => f.Target, f => f.Path[0], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The consumer's replica classes (warehouse §4.2.1): every type an <c>[assembly: SparkReplicaTypes]</c> lists, read
/// from the generated constants — no member scanning, no runtime script building. Also the framework's
/// <see cref="ISparkReplicaTypes"/>, so the unchecked write paths refuse replicas.
/// </summary>
internal sealed class ReplicaCatalog : ISparkReplicaTypes
{
    private readonly Lazy<IReadOnlyList<ReplicaDescriptor>> all;
    private readonly HashSet<Type> types = [];

    public ReplicaCatalog(IOptions<SparkReplicationOptions> options)
    {
        all = new(() =>
        {
            var found = Discover(options.Value).ToList();
            lock (types)
            {
                foreach (var descriptor in found)
                    types.Add(descriptor.Type);
            }
            return found;
        });
    }

    /// <summary>For tests: a catalog over the given replica types.</summary>
    internal ReplicaCatalog(SparkReplicationOptions options, params Type[] replicaTypes)
    {
        var found = replicaTypes.Select(t => Describe(t, options)).ToList();
        foreach (var descriptor in found)
            types.Add(descriptor.Type);
        all = new(found);
    }

    public IReadOnlyList<ReplicaDescriptor> All => all.Value;

    public bool IsReplica(Type entityType)
    {
        _ = all.Value;
        for (var current = entityType; current is not null; current = current.BaseType)
        {
            if (types.Contains(current))
                return true;
        }
        return false;
    }

    public ReplicaDescriptor? For(Type entityType)
        => All.FirstOrDefault(d => d.Type.IsAssignableFrom(entityType));

    public ReplicaDescriptor? ForTarget(string targetCollection)
        => All.FirstOrDefault(d => string.Equals(d.TargetCollection, targetCollection, StringComparison.Ordinal));

    /// <summary>The replicas read from <paramref name="owner"/>.</summary>
    public IEnumerable<ReplicaDescriptor> FromOwner(string owner) => All.Where(d => d.IsSource(owner));

    /// <summary>Every owner this module reads from.</summary>
    public IEnumerable<string> Owners => All.SelectMany(d => d.Sources).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What refuses startup: a replica no configuration names a source for, a target collection the store names
    /// differently (the generator's default pluralisation is only a guess, the store is the truth), two replicas of one
    /// target collection, or constants that no longer render the same (edited by hand).
    /// </summary>
    public IEnumerable<string> Problems(IDocumentStore store)
    {
        foreach (var descriptor in All)
        {
            var name = descriptor.Type.FullName;
            if (descriptor.Sources.Count == 0)
                yield return $"{name} names no source module: set SourceModule on [Replicated], or Spark:Replication:Sources:{descriptor.TargetCollection}.";

            var collection = store.Conventions.FindCollectionName(descriptor.Type);
            if (!string.Equals(collection, descriptor.TargetCollection, StringComparison.Ordinal))
                yield return $"{name} replicates into '{descriptor.TargetCollection}', but this store names its collection '{collection}': set TargetCollection = \"{collection}\" on [Replicated].";

            if (ReplicaScriptRenderer.RenderScript(descriptor.Parsed) != descriptor.Script
                || ReplicaScriptRenderer.Hash(descriptor.Manifest, descriptor.Script) != descriptor.Hash)
                yield return $"{name}'s generated replication constants do not render the same; rebuild the library that declares it.";
        }

        foreach (var duplicate in All.GroupBy(d => d.TargetCollection, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"Replica collection '{duplicate.Key}' is declared by {string.Join(" and ", duplicate.Select(d => d.Type.FullName))}.";
    }

    /// <summary>
    /// The set hash per owner (WD15): over the sorted <c>target=hash</c> pairs of every replica read from it, so any
    /// change, addition or removal changes it.
    /// </summary>
    public string SetHash(string owner)
    {
        var lines = FromOwner(owner)
            .OrderBy(d => d.TargetCollection, StringComparer.Ordinal)
            .Select(d => $"{d.TargetCollection}={d.Hash}");
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }

    private static IEnumerable<ReplicaDescriptor> Discover(SparkReplicationOptions options)
    {
        var seen = new HashSet<Type>();
        foreach (var assembly in CandidateAssemblies())
        {
            IEnumerable<SparkReplicaTypesAttribute> declared;
            try { declared = assembly.GetCustomAttributes<SparkReplicaTypesAttribute>(); }
            catch (Exception) { continue; }

            foreach (var type in declared.SelectMany(a => a.Types))
            {
                if (seen.Add(type))
                    yield return Describe(type, options);
            }
        }
    }

    /// <summary>
    /// Every loaded assembly, plus the entry assembly's references that reference Replication.Abstractions (a replica
    /// library may not be loaded yet at startup).
    /// </summary>
    private static IEnumerable<Assembly> CandidateAssemblies()
    {
        var abstractions = typeof(ReplicatedAttribute).Assembly.GetName().Name;
        var result = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            result.TryAdd(loaded.FullName ?? loaded.GetName().Name!, loaded);

        var queue = new Queue<Assembly>();
        if (Assembly.GetEntryAssembly() is { } entry)
            queue.Enqueue(entry);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!visited.Add(reference.FullName) || IsFramework(reference.Name))
                    continue;
                Assembly loaded;
                try { loaded = Assembly.Load(reference); }
                catch (Exception) { continue; }
                result.TryAdd(loaded.FullName ?? reference.FullName, loaded);
                if (loaded.GetReferencedAssemblies().Any(r => r.Name == abstractions) || loaded.GetName().Name!.StartsWith("MintPlayer.", StringComparison.Ordinal) == false)
                    queue.Enqueue(loaded);
            }
        }
        return result.Values;
    }

    private static bool IsFramework(string? name)
        => name is null || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || name is "netstandard" or "mscorlib" || name.StartsWith("Raven.", StringComparison.Ordinal) || name.StartsWith("Sparrow", StringComparison.Ordinal);

    internal static ReplicaDescriptor Describe(Type type, SparkReplicationOptions options)
    {
        string Text(string field) => Constant(type, field) as string
            ?? throw new InvalidOperationException($"{type.FullName} has no generated {field}: is the class partial, and does its library reference MintPlayer.Spark.LibraryGenerators?");

        var manifest = Text("ReplicationManifest");
        if (!ReplicaScriptRenderer.TryParse(manifest, out var parsed, out var error))
            throw new InvalidOperationException($"{type.FullName}'s generated manifest is invalid: {error}");

        var target = Text("ReplicationTargetCollection");
        var declared = Constant(type, "ReplicationSourceModule") as string;
        IReadOnlyList<string> sources = declared is { Length: > 0 }
            ? [declared]
            : options.Sources.TryGetValue(target, out var configured) ? [.. configured.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)] : [];

        return new ReplicaDescriptor(
            type,
            Text("ReplicationSourceCollection"),
            target,
            Constant(type, "ReplicationWriteBack") is true,
            manifest,
            Text("ReplicationScript"),
            Text("ReplicationHash"),
            parsed!,
            sources);
    }

    private static object? Constant(Type type, string name)
        => type.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly) is { IsLiteral: true } field
            ? field.GetRawConstantValue()
            : null;
}
