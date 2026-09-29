namespace MintPlayer.Spark.Moderation;

/// <summary>
/// Opts an entity into moderation: it can be voted on, flagged, locked, and its author earns (or
/// loses) reputation for it (#460, item 12).
/// </summary>
/// <remarks>
/// <para>
/// Both members are the <b>framework's</b>. The Moderation interceptor stamps them on create
/// (<see cref="AuthorId"/> = the current user's id, <see cref="PostedAt"/> = now) and restores the
/// stored values on every later write, whatever the client posted — an author field a client could
/// edit would let anybody collect the reputation of somebody else's post.
/// </para>
/// <para>
/// ⚠️ An <b>id</b>, never a display name (D8): an id survives a rename and can be shown as
/// "deleted user" once the account is gone.
/// </para>
/// </remarks>
public interface IModeratable
{
    /// <summary>The id of the user who created the entity. Stamped by the framework, immutable after create.</summary>
    string? AuthorId { get; set; }

    /// <summary>When the entity was created. Stamped by the framework; the fast-voting rule measures votes against it.</summary>
    DateTimeOffset? PostedAt { get; set; }
}
