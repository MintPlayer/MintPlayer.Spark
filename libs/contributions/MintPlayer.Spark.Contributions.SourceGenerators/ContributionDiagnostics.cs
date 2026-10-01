using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.Contributions.SourceGenerators;

/// <summary>
/// The rules of PRD T2/T4 (and Q4). Every one is reported on the <c>[Contribution]</c> property —
/// a location the analyzed compilation always owns, even when the element type lives in another
/// project or only in metadata (see docs/diagnostics.md, "Report at a location the analyzed symbol
/// owns"). Ids SPARK025–SPARK029 and SPARK031–SPARK035; SPARK030 is an MSBuild warning of
/// MintPlayer.Spark.Authorization.
/// </summary>
internal static class ContributionDiagnostics
{
    private const string Category = "Contributions";

    /// <summary>Diagnostic property: the element type's metadata name, for the SPARK031 code fix.</summary>
    internal const string ElementMetadataNameProperty = "SparkContributionElement";

    internal static readonly DiagnosticDescriptor SlotTypeNotAllowed = new(
        id: "SPARK025",
        title: "Contribution slot type has no stable text form",
        messageFormat: "Slot '{0}' of '{1}' is a '{2}'. A slot must be string, an enum, an integral type (byte, sbyte, short, ushort, int, uint, long, ulong), Guid or bool, or their nullable forms: the slot value is part of the document id, so it needs one culture-independent text form.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "float, double and decimal have no exact text form, and DateTime/DateTimeOffset depend on precision, offset and culture. Use a string or an integral slot (e.g. a year) instead.");

    internal static readonly DiagnosticDescriptor CardinalityMismatch = new(
        id: "SPARK026",
        title: "Contribution property's cardinality does not match its slots",
        messageFormat: "'{0}' {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A collection holds one current version per slot combination, so its element needs at least one [ContributionSlot]. A single-valued property has exactly one current version per target, so its element must have no slots.");

    internal static readonly DiagnosticDescriptor NoValueProperties = new(
        id: "SPARK027",
        title: "Contribution element has no value properties",
        messageFormat: "Element '{0}' of '{1}' has no value properties: every public settable property is a [ContributionSlot], so a contribution would carry nothing",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor OwnerNotAnEntity = new(
        id: "SPARK028",
        title: "[Contribution] on a type that is not a persistent-object entity",
        messageFormat: "'{0}' declares [Contribution] but has no public string 'Id' property, so it is not a document Spark can load as a persistent object; the contribution ids are built from the target's id",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The rule is structural: a Spark entity is a RavenDB document, and every document type has a string Id. The generated code is still emitted.");

    internal static readonly DiagnosticDescriptor MissingJsonIgnore = new(
        id: "SPARK029",
        title: "[Contribution] property is stored on the target",
        messageFormat: "'{0}' is a [Contribution] property without [Newtonsoft.Json.JsonIgnore]: its rows would be stored on the target document as well as in the contribution documents, and every lyric edit would rewrite the target",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The RavenDB store serializes with Newtonsoft, so it must be Newtonsoft's [JsonIgnore]; System.Text.Json's has no effect on storage. [IgnoreProperty] would remove the attribute from the model entirely.");

    internal static readonly DiagnosticDescriptor ElementNotPartial = new(
        id: "SPARK031",
        title: "Contribution element must be partial",
        messageFormat: "'{0}' must be declared partial: the generator adds {1} to it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nothing is generated for the declaration until the element (and every type containing it) is partial.");

    internal static readonly DiagnosticDescriptor ElementExternal = new(
        id: "SPARK032",
        title: "Contribution element is declared in another assembly",
        messageFormat: "Element '{0}' of '{1}' is declared in assembly '{2}', and a generator can only add members to types of its own compilation; nothing is generated for this declaration. Move the element into this project.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor NotSoftDeletable = new(
        id: "SPARK033",
        title: "Contributions will be hard-deleted",
        messageFormat: "MintPlayer.Spark.SoftDelete is not referenced, so '{0}' is not ISoftDeletable: hiding a contribution deletes it for good, with no restore and no history of hidden versions",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reference MintPlayer.Spark.SoftDelete and the generated contribution type becomes ISoftDeletable automatically (PRD Q4).");

    internal static readonly DiagnosticDescriptor UnsupportedShape = new(
        id: "SPARK034",
        title: "Unsupported [Contribution] declaration",
        messageFormat: "'{0}': {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The property must be an instance property with a getter on a non-generic class; its type T, T[], List<T>, IList<T>, IReadOnlyList<T>, ICollection<T> or IEnumerable<T>, settable unless it is a List/IList/ICollection; T a non-abstract, non-generic class or record with a parameterless constructor; slots public with a getter and a setter.");

    internal static readonly DiagnosticDescriptor ReservedMember = new(
        id: "SPARK035",
        title: "Contribution element clashes with generated members",
        messageFormat: "Element '{0}' of '{1}': {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator adds Key (the row key) and the attribution properties to the element, and Id, TargetId, ContributorId, ContributionId, UpdatedAt, ContributionCount and the ISoftDeletable members to the generated documents. [ValueObject] would register a second, Guid row key.");
}
