namespace MintPlayer.Spark.History;

/// <summary>
/// An entity the History package stamps on every write through the Spark pipeline: who created it
/// and when, who changed it last and when. The union of <see cref="IAuditCreated"/> and
/// <see cref="IAuditModified"/>; implement either half alone to keep only that pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>User ids only</b> (#460, D8) — never a name or an email. A name is resolved at read time (an
/// <c>IHistoryUserNameResolver</c> (MintPlayer.Spark.History)), so deleting an account leaves an id that resolves to
/// nothing ("deleted user") instead of personal data spread over every document it touched.
/// </para>
/// <para>
/// In a <c>partial</c> type of an entity library that references <c>MintPlayer.Spark.LibraryGenerators</c>,
/// the members are generated (#271): declare the interface and nothing else.
/// </para>
/// </remarks>
public interface IAuditable : IAuditCreated, IAuditModified
{
}

/// <summary>
/// An entity that records who created it and when, stamped by the History package on the first write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CreatedBy"/> and <see cref="CreatedAt"/> are <b>immutable</b> after the create: every
/// later save — edit, revert, restore — keeps the stored values, whatever the client posted, so
/// authorship (and anything derived from it, such as reputation) cannot be claimed by editing.
/// </para>
/// <para>Implement the members as public read/write properties; Raven stores only those.</para>
/// </remarks>
public interface IAuditCreated
{
    /// <summary>The creating user's id; <see langword="null"/> for a write with no user (the system).</summary>
    string? CreatedBy { get; set; }

    /// <summary>When the entity was created.</summary>
    DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>
/// An entity that records who changed it last and when, stamped by the History package on every write
/// that changes it (a soft delete included).
/// </summary>
/// <remarks>
/// A write a replica forwards (<c>Sync</c>) is stamped with the user the replica states it was made for
/// (#271, F2), and left as stored when it states none.
/// </remarks>
public interface IAuditModified
{
    /// <summary>The id of the user who saved it last; <see langword="null"/> for a system write.</summary>
    string? ModifiedBy { get; set; }

    /// <summary>When it was saved last.</summary>
    DateTimeOffset? ModifiedAt { get; set; }
}
