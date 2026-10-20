using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Emits a <c>partial</c> half with the replication constants of every <c>[Replicated]</c> class
/// <see cref="ReplicatedGenerator"/> found without a problem. A class with one is skipped here and
/// reported by <see cref="ReplicatedReporter"/>, never dropped quietly.
/// </summary>
public class ReplicatedMembersProducer : Producer
{
    private const string Marker = "global::MintPlayer.Spark.Replication.Abstractions.IReplicatedEntity";

    private readonly ImmutableArray<ReplicaInfo> replicas;

    public ReplicatedMembersProducer(ImmutableArray<ReplicaInfo> replicas, string rootNamespace)
        : base(rootNamespace, "SparkReplicas.g.cs")
    {
        this.replicas = replicas;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var emitted = replicas.Where(static r => r.Problems.Count == 0).ToArray();
        if (emitted.Length == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        foreach (var group in emitted.GroupBy(static r => r.PathSpec?.ContainingNamespace ?? string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IDisposable? namespaceBlock = string.IsNullOrEmpty(group.Key)
                ? null
                : writer.OpenBlock($"namespace {group.Key}");

            foreach (var replica in group)
            {
                using var parents = writer.OpenPathSpec(replica.PathSpec);
                using (writer.OpenBlock($"{replica.Header} : {Marker}"))
                {
                    var module = replica.SourceModule is null ? "null" : Literal(replica.SourceModule);
                    writer.WriteLine($"public const string ReplicationSourceCollection = {Literal(replica.SourceCollection)};");
                    writer.WriteLine($"public const string ReplicationTargetCollection = {Literal(replica.TargetCollection)};");
                    writer.WriteLine($"public const string? ReplicationSourceModule = {module};");
                    writer.WriteLine($"public const bool ReplicationWriteBack = {(replica.WriteBack ? "true" : "false")};");
                    writer.WriteLine($"public const string ReplicationManifest = {Literal(replica.Manifest)};");
                    writer.WriteLine($"public const string ReplicationScript = {Literal(replica.Script)};");
                    writer.WriteLine($"public const string ReplicationHash = {Literal(replica.Hash)};");
                }
            }

            namespaceBlock?.Dispose();
        }
    }

    /// <summary>A C# string literal, escaped by Roslyn itself (the script holds quotes and newlines).</summary>
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}

/// <summary>
/// Emits the one <c>[assembly: SparkReplicaTypes(...)]</c> of the compilation, listing every replica that
/// got its constants, sorted by full name. The runtime lists replicas from it without scanning members.
/// </summary>
public class ReplicaTypesProducer : Producer
{
    private readonly ImmutableArray<ReplicaInfo> replicas;

    public ReplicaTypesProducer(ImmutableArray<ReplicaInfo> replicas, string rootNamespace)
        : base(rootNamespace, "SparkReplicaTypes.g.cs")
    {
        this.replicas = replicas;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        // Already ordered by full name (ReplicatedGenerator.Distinct).
        var types = replicas
            .Where(static r => r.Problems.Count == 0)
            .Select(static r => $"typeof({r.FullyQualifiedName})")
            .ToArray();
        if (types.Length == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine($"[assembly: global::MintPlayer.Spark.Replication.Abstractions.SparkReplicaTypes({string.Join(", ", types)})]");
    }
}
