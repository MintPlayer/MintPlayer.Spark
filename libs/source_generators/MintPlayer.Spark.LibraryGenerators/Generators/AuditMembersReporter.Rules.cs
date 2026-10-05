using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

public partial class AuditMembersReporter
{
    /// <summary>
    /// Error, like SPARK016: the compiler already refuses the type (CS0535, the interface is not
    /// implemented), and this says what to do about it instead of listing four missing members.
    /// </summary>
    internal static readonly DiagnosticDescriptor AuditTypeMustBePartialRule = new(
        id: "SPARK038",
        title: "A type implementing a framework-stamped interface must be partial",
        messageFormat: "'{0}' implements a framework-stamped interface but does not declare {1} — declare '{0}' (and every type containing it) 'partial' so they are generated, or declare them yourself",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "IAuditCreated, IAuditModified (and so IAuditable), ISoftDeletable and IModeratable are stamped by the framework, and their members are generated into a partial half of the type that implements them. A type that is not partial cannot be given those members.");

    /// <summary>
    /// Info: nothing is wrong, but nothing else would tell the author why the user field is not a link
    /// to the user. Adding the reference later turns it into one (and moves the model hash).
    /// </summary>
    internal static readonly DiagnosticDescriptor UserIdWithoutReferenceRule = new(
        id: "SPARK040",
        title: "User-id member generated without [Reference]",
        messageFormat: "'{0}': {1} generated as plain strings, because this project does not reference MintPlayer.Spark.Authorization.Abstractions — reference it to make them references to SparkUser",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The generated user-id members (CreatedBy, ModifiedBy, DeletedBy, AuthorId) store a SparkUser id. They are generated as [Reference(typeof(SparkUser))] when the project can see SparkUser and the Reference attribute, so the page shows the user's name; otherwise they stay plain strings.");
}
