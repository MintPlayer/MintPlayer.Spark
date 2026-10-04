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

/// <summary>The <c>security.json</c> rights the History package asks for, per entity type.</summary>
public static class HistoryRights
{
    /// <summary><c>History/T</c>: list a row's revisions and read one.</summary>
    public const string History = "History";

    /// <summary><c>Revert/T</c> (together with <c>Edit/T</c>): save a row back to one of its revisions.</summary>
    public const string Revert = "Revert";
}

/// <summary>
/// Resolves user ids to display names for revision lists (#460, D8: names at read time). Optional —
/// without one, a revision carries the id only. Register one with
/// <c>AddHistoryUserNameResolver&lt;T&gt;()</c>; an app on Spark's Identity typically looks the ids up
/// in its user store.
/// </summary>
public interface IHistoryUserNameResolver
{
    /// <summary>
    /// A name per id it knows. An id missing from the result is shown as a deleted user. Called once
    /// per revision list, with every distinct id on the page.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken);
}
