using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// One embedded type that is the element of a collection somewhere in this compilation, and so needs
/// a stable per-row key.
/// </summary>
/// <remarks>
/// Everything here is a string, bool or <see cref="Tools.PathSpec"/> — no <c>ISymbol</c> — so the
/// value stays comparable and the incremental pipeline keeps caching.
/// </remarks>
[AutoValueComparer]
public partial class ValueObjectInfo
{
    /// <summary>Fully qualified name, used to distinct the set and to key the hint name.</summary>
    public string FullyQualifiedName { get; set; } = string.Empty;

    /// <summary>Bare type name, for the <c>partial class X</c> the generator reopens.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Namespace and containing types, reconstructed from the symbol rather than the file path.</summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>
    /// Whether every declaration of the type carries <c>partial</c>. False means the generator cannot
    /// reopen it and must report instead of emitting.
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Whether the type already declares an <c>Id</c>. Those are skipped: the two in this workspace
    /// are load-bearing — one is a GitHub option id assigned from the API, the other is derived from
    /// an event type in a save hook — and overwriting either with a Guid breaks behaviour.
    /// </summary>
    public bool DeclaresOwnId { get; set; }

    /// <summary>Where to point a diagnostic when <see cref="IsPartial"/> is false.</summary>
    public LocationKey? Location { get; set; }

    /// <summary>The type whose collection property introduced this one.</summary>
    /// <remarks>
    /// Kept so reachability can be computed once every edge is known. A library holds persisted
    /// types that are not part of the Spark model — high-volume coverage rows, for instance — and
    /// their collections must not be keyed.
    /// </remarks>
    public string OwnerFullyQualifiedName { get; set; } = string.Empty;

    /// <summary>Whether the owner is a document root, i.e. carries <c>[GenerateIndex]</c>.</summary>
    public bool OwnerIsRoot { get; set; }
}
