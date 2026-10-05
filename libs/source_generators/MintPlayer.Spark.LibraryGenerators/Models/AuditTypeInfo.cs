using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.LibraryGenerators.Models;

/// <summary>
/// One type implementing a framework-stamped contract (<c>IAuditCreated</c>, <c>IAuditModified</c>,
/// <c>ISoftDeletable</c>, <c>IModeratable</c>) that leaves at least one of its members undeclared.
/// </summary>
/// <remarks>
/// Strings, bools and <see cref="Tools.PathSpec"/> only — no <c>ISymbol</c> — so the incremental pipeline
/// keeps caching.
/// </remarks>
[GenerateEquality]
public partial class AuditTypeInfo
{
    /// <summary>Fully qualified name, used to distinct the set.</summary>
    public string FullyQualifiedName { get; set; } = string.Empty;

    /// <summary>Bare type name, for the diagnostic.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The reopening header: <c>partial record Line</c>, <c>partial class Box&lt;T&gt;</c>.</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>Namespace and containing types, reconstructed from the symbol.</summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>Whether the type and every type containing it are declared <c>partial</c>.</summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// The undeclared members to emit, comma-separated, in the contract table's order
    /// (a string rather than an array so the value stays comparable).
    /// </summary>
    public string MissingMembers { get; set; } = string.Empty;

    /// <summary>Where to point a diagnostic.</summary>
    public LocationKey? Location { get; set; }
}
