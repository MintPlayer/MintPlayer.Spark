using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.LibraryGenerators.Models;

/// <summary>
/// One get-only member of a stored type: RavenDB writes it into the document, so it gets a backing field the SQL
/// providers store it through (#536 O13).
/// </summary>
[GenerateEquality]
public partial class StoredGetterInfo
{
    /// <summary>The declaring type's fully qualified name, to group the members per type.</summary>
    public string TypeFullyQualifiedName { get; set; } = string.Empty;

    /// <summary>The declaring type's bare name, for the diagnostics.</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The reopening header (<c>partial class Person</c>).</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>Namespace and containing types.</summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>Whether the type and its containing types are partial on every declaration.</summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Whether another rule already demands <c>partial</c>: SPARK064 for a type with its own <c>Id&lt;Self&gt;</c>,
    /// SPARK016 for a <c>[ValueObject]</c>.
    /// </summary>
    public bool PartialReportedElsewhere { get; set; }

    /// <summary>A record compares every field, so a generated one would change its equality.</summary>
    public bool IsRecord { get; set; }

    /// <summary>The member's name.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>The member's type, as the field declares it (no nullable annotation: the field is in a disabled context).</summary>
    public string FieldType { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the getter body's tokens.</summary>
    public string Formula { get; set; } = string.Empty;

    /// <summary>The clock member the getter reads (<c>DateTime.UtcNow</c>), if any (SPARK068).</summary>
    public string? ReadsClock { get; set; }

    /// <summary>Another stored type the getter reads a value of, if any (SPARK068).</summary>
    public string? ReadsEntity { get; set; }

    /// <summary>Where to point a diagnostic: the member.</summary>
    public LocationKey? Location { get; set; }
}
