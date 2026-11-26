using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.LibraryGenerators.Models;

/// <summary>
/// One entity whose own <c>Id</c> is an <c>Id&lt;Self&gt;</c>, and therefore one hidden <c>__DocumentId</c>
/// member to emit (#536 D23 rule 6).
/// </summary>
[GenerateEquality]
public partial class DocumentIdInfo
{
    /// <summary>Fully qualified name, used to distinct the set.</summary>
    public string FullyQualifiedName { get; set; } = string.Empty;

    /// <summary>Bare type name, for the diagnostic.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The reopening header (<c>partial class Person</c>).</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>Namespace and containing types.</summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>Whether the type and its containing types are partial on every declaration.</summary>
    public bool IsPartial { get; set; }

    /// <summary>Where to point the diagnostic when <see cref="IsPartial"/> is false.</summary>
    public LocationKey? Location { get; set; }
}
