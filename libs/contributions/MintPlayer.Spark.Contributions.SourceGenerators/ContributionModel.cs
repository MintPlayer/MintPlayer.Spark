namespace MintPlayer.Spark.Contributions.SourceGenerators;

/// <summary>How the <c>[Contribution]</c> property holds its element.</summary>
internal enum CollectionShape
{
    Single,
    Array,
    List,
    IList,
    IReadOnlyList,
    ICollection,
    IEnumerable,
}

/// <summary>The T2 slot kinds; each has one <c>ContributionSlotFormat</c> call.</summary>
internal enum SlotKind
{
    String,
    Enum,
    Integral,
    Guid,
    Bool,
}

/// <summary>One <c>[ContributionSlot]</c> property.</summary>
/// <param name="Name">Property name.</param>
/// <param name="Type">Fully qualified type, nullable annotation included.</param>
/// <param name="Kind">Slot kind.</param>
/// <param name="EnumType">For an enum slot, the fully qualified enum type without <c>?</c>.</param>
/// <param name="ParameterName">The <c>GetId</c> parameter name (camel case, escaped).</param>
/// <param name="Initializer">The property initializer on the generated documents (<c> = "";</c> or empty).</param>
internal sealed record SlotModel(string Name, string Type, SlotKind Kind, string? EnumType, string ParameterName, string Initializer)
{
    /// <summary>The expression that formats <paramref name="expression"/> as this slot's id segment.</summary>
    public string Format(string expression) => Kind == SlotKind.Enum
        ? $"global::MintPlayer.Spark.Contributions.ContributionSlotFormat.FormatEnum<{EnumType}>({expression})"
        : $"global::MintPlayer.Spark.Contributions.ContributionSlotFormat.Format({expression})";
}

/// <summary>One value (non-slot) property of the element.</summary>
internal sealed record ValueModel(string Name, string Type, string Initializer);

/// <summary>The element's declaration, for the partial the generator adds to it.</summary>
/// <param name="FullyQualified">Fully qualified name (<c>global::TestApp.Lyrics</c>).</param>
/// <param name="Namespace">Containing namespace, or empty for the global namespace.</param>
/// <param name="Declaration">The partial header, e.g. <c>partial class Lyrics</c> or <c>partial record Lyrics</c>.</param>
/// <param name="ContainingDeclarations">Partial headers of the containing types, outermost first.</param>
internal sealed record ElementDeclaration(string FullyQualified, string Namespace, string Declaration, EquatableArray<string> ContainingDeclarations);

/// <summary>Everything the generator emits for one <c>[Contribution]</c> property. Value-equal, so the pipeline caches.</summary>
internal sealed record ContributionModel(
    string Namespace,
    string OwnerName,
    string OwnerType,
    string PropertyName,
    ElementDeclaration Element,
    CollectionShape Shape,
    bool PropertyWritable,
    EquatableArray<SlotModel> Slots,
    EquatableArray<ValueModel> Values,
    int Attribution,
    bool IsSoftDeletable,
    bool EmitSoftDeleteMembers,
    bool IsPublic,
    string ShapeHash)
{
    public const int AttributionContributor = 1;
    public const int AttributionUpdatedAt = 2;
    public const int AttributionHistory = 4;

    public string BaseName => OwnerName + PropertyName;
    public string ContributionName => BaseName + "Contribution";
    public string CurrentName => BaseName + "Current";
    public string MetadataName => BaseName + "ContributionMetadata";
    public string QueryName => BaseName + "Contributions";
    public bool IsCollection => Shape != CollectionShape.Single;

    public bool Has(int flag) => (Attribution & flag) != 0;

    private string Prefix => string.IsNullOrEmpty(Namespace) ? "global::" : $"global::{Namespace}.";
    public string ContributionType => Prefix + ContributionName;
    public string CurrentType => Prefix + CurrentName;
    public string MetadataType => Prefix + MetadataName;
}
