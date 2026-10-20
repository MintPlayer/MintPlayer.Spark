using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.Replication.Abstractions.Rendering;

// ⚠️ Linked into MintPlayer.Spark.LibraryGenerators (netstandard2.0), which renders the same manifest at
// build time. Keep this file free of anything netstandard2.0 lacks: records, ranges, Span-only APIs.

/// <summary>One mapped replica field: the replica property and the source path it is read from.</summary>
public sealed class ReplicaField
{
    public ReplicaField(string target, IReadOnlyList<string> path)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Path = (path ?? throw new ArgumentNullException(nameof(path))).ToArray();
    }

    /// <summary>The replica's property, written by the script.</summary>
    public string Target { get; }

    /// <summary>The source path, one member name per segment.</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>The source path joined with dots, as <c>[ReplicatedFrom]</c> states it.</summary>
    public string PathText => string.Join(".", Path);
}

/// <summary>
/// The structured form of a replica's ETL script: which source collection is read, which consumer
/// collection is written, and the target ← source path of every field (warehouse WD14).
/// </summary>
/// <remarks>
/// The generator emits it as <c>ReplicationManifest</c> (<see cref="ReplicaScriptRenderer.ToJson"/>); the
/// owner parses it back (<see cref="ReplicaScriptRenderer.TryParse"/>) and re-renders the script byte for
/// byte, so the owner never runs JavaScript a consumer wrote.
/// </remarks>
public sealed class ReplicaManifest
{
    /// <summary>The manifest format, written as <c>"v"</c>. A parser refuses any other value.</summary>
    public const int FormatVersion = 1;

    public ReplicaManifest(string sourceCollection, string targetCollection, IReadOnlyList<ReplicaField> fields)
    {
        SourceCollection = sourceCollection ?? throw new ArgumentNullException(nameof(sourceCollection));
        TargetCollection = targetCollection ?? throw new ArgumentNullException(nameof(targetCollection));
        Fields = (fields ?? throw new ArgumentNullException(nameof(fields))).ToArray();
    }

    /// <summary>The owner's collection the script reads.</summary>
    public string SourceCollection { get; }

    /// <summary>The consumer's collection the script loads into.</summary>
    public string TargetCollection { get; }

    /// <summary>The mapped fields, in declaration order.</summary>
    public IReadOnlyList<ReplicaField> Fields { get; }
}
