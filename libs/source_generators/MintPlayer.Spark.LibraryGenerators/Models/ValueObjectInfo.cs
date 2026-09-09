using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.LibraryGenerators.Models;

/// <summary>
/// One type decorated <c>[ValueObject]</c>, and therefore one row type needing a stable key.
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
    /// The property marked <c>[ValueKey]</c>, or <see langword="null"/> when the type has none and a
    /// key is to be generated for it.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not "declares an <c>Id</c>". An earlier version keyed off that, conflating <em>do not
    /// generate a key</em> with <em>has no key</em>, and so dropped two correctly keyed types out of
    /// the registry entirely. Absence from the registry is not neutral — it makes the type's
    /// collections unjudgeable at save time.
    /// </remarks>
    public string? ExistingKeyProperty { get; set; }

    /// <summary>Where to point a diagnostic when <see cref="IsPartial"/> is false.</summary>
    public LocationKey? Location { get; set; }
}
