using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.LibraryGenerators.Models;

/// <summary>
/// One class decorated <c>[Replicated]</c>: either everything the producer emits for it, already rendered,
/// or the problems that stop it from being emitted (warehouse PRD §4.2.1).
/// </summary>
/// <remarks>
/// Strings, bools and <see cref="Tools.PathSpec"/> only — no <c>ISymbol</c> — so the incremental pipeline
/// keeps caching. The manifest, script and hash are rendered in the transform, so a keystroke elsewhere
/// re-runs nothing.
/// </remarks>
[GenerateEquality]
public partial class ReplicaInfo
{
    /// <summary>Fully qualified name (<c>global::NS.Car</c>), used to distinct and sort the set.</summary>
    public string FullyQualifiedName { get; set; } = string.Empty;

    /// <summary>Bare type name, for diagnostics.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The reopening header, <c>partial class Car</c>.</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>Namespace and containing types, reconstructed from the symbol.</summary>
    public PathSpec? PathSpec { get; set; }

    public string SourceCollection { get; set; } = string.Empty;

    public string TargetCollection { get; set; } = string.Empty;

    /// <summary>The owner module, or <see langword="null"/> when the host names it in configuration.</summary>
    public string? SourceModule { get; set; }

    public bool WriteBack { get; set; }

    /// <summary><c>ReplicaScriptRenderer.ToJson</c> of the manifest; empty when there are problems.</summary>
    public string Manifest { get; set; } = string.Empty;

    /// <summary><c>ReplicaScriptRenderer.RenderScript</c> of the manifest; empty when there are problems.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary><c>ReplicaScriptRenderer.Hash</c> of the two; empty when there are problems.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Everything wrong with the declaration. Non-empty means nothing is generated for the type.</summary>
    public List<ReplicaProblem> Problems { get; set; } = new();
}

/// <summary>One diagnostic about a <c>[Replicated]</c> class, reported by <c>ReplicatedReporter</c>.</summary>
[GenerateEquality]
public partial class ReplicaProblem
{
    /// <summary>The diagnostic id, SPARK065–SPARK068.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The message arguments after the type name, joined by <c>\u001F</c> (a string stays comparable).</summary>
    public string Arguments { get; set; } = string.Empty;

    public LocationKey? Location { get; set; }
}
