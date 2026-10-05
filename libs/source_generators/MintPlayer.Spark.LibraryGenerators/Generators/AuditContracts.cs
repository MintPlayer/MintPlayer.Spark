namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// The framework-stamped contracts whose members <see cref="AuditMembersGenerator"/> fills in, and their
/// members exactly as the interfaces declare them (#271).
/// </summary>
/// <remarks>
/// Matched by name, never by symbol: the interfaces live in other assemblies, and a symbol lookup by
/// metadata name returns nothing when two references define the name. <c>IAuditable</c> has no row: it is
/// the union of <c>IAuditCreated</c> and <c>IAuditModified</c> and arrives through them.
/// </remarks>
internal static class AuditContracts
{
    public const string SparkUser = "MintPlayer.Spark.Authorization.Identity.SparkUser";
    public const string ReferenceAttribute = "MintPlayer.Spark.Abstractions.ReferenceAttribute";

    public static readonly Contract[] All =
    [
        new("MintPlayer.Spark.History.IAuditCreated",
        [
            new("CreatedBy", "string?", isUserId: true, "The creating user's id. Stamped by History on the first write, never changed afterwards."),
            new("CreatedAt", "global::System.DateTimeOffset?", isUserId: false, "When the entity was created. Stamped by History."),
        ]),
        new("MintPlayer.Spark.History.IAuditModified",
        [
            new("ModifiedBy", "string?", isUserId: true, "The id of the user who changed it last. Stamped by History."),
            new("ModifiedAt", "global::System.DateTimeOffset?", isUserId: false, "When it was changed last. Stamped by History."),
        ]),
        new("MintPlayer.Spark.SoftDelete.ISoftDeletable",
        [
            new("IsDeleted", "bool", isUserId: false, "Whether it was deleted. A deleted row is hidden everywhere until it is restored."),
            new("DeletedAt", "global::System.DateTimeOffset?", isUserId: false, "When it was deleted. Stamped by SoftDelete."),
            new("DeletedBy", "string?", isUserId: true, "The id of the user who deleted it. Stamped by SoftDelete."),
            new("DeleteReason", "string?", isUserId: false, "Why it was deleted, when a reason was given."),
        ]),
        new("MintPlayer.Spark.Moderation.IModeratable",
        [
            new("AuthorId", "string?", isUserId: true, "The author's user id. Stamped by Moderation on create, never changed afterwards."),
            new("PostedAt", "global::System.DateTimeOffset?", isUserId: false, "When it was posted. Stamped by Moderation."),
        ]),
    ];

    public static Member? FindMember(string name)
    {
        foreach (var contract in All)
            foreach (var member in contract.Members)
                if (member.Name == name)
                    return member;
        return null;
    }

    internal sealed class Contract(string interfaceName, Member[] members)
    {
        public string InterfaceName { get; } = interfaceName;
        public Member[] Members { get; } = members;
    }

    internal sealed class Member(string name, string type, bool isUserId, string summary)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public bool IsUserId { get; } = isUserId;
        public string Summary { get; } = summary;
    }
}
