namespace MintPlayer.Spark.History;

/// <summary>
/// An entity the History package stamps on every write through the Spark pipeline: who created it
/// and when, who changed it last and when.
/// </summary>
/// <remarks>
/// <para>
/// <b>User ids only</b> (#460, D8) — never a name or an email. A name is resolved at read time (an
/// <c>IHistoryUserNameResolver</c> (MintPlayer.Spark.History)), so deleting an account leaves an id that resolves to
/// nothing ("deleted user") instead of personal data spread over every document it touched.
/// </para>
/// <para>
/// <see cref="CreatedBy"/> and <see cref="CreatedAt"/> are <b>immutable</b> after the create: every
/// later save — edit, revert, restore — keeps the stored values, whatever the client posted, so
/// authorship (and anything derived from it, such as reputation) cannot be claimed by editing.
/// A <c>Sync</c> (a write replicated from the owner module) is left as the owner stamped it.
/// </para>
/// <para>Implement the four members as public read/write properties; Raven stores only those.</para>
/// </remarks>
public interface IAuditable
{
    /// <summary>The creating user's id; <see langword="null"/> for a write with no user (the system).</summary>
    string? CreatedBy { get; set; }

    /// <summary>When the entity was created.</summary>
    DateTimeOffset? CreatedAt { get; set; }

    /// <summary>The id of the user who saved it last; <see langword="null"/> for a system write.</summary>
    string? ModifiedBy { get; set; }

    /// <summary>When it was saved last.</summary>
    DateTimeOffset? ModifiedAt { get; set; }
}
