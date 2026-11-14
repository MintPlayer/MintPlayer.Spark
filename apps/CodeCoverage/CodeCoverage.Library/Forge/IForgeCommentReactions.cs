using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// The App's own reactions on a comment that holds a bot command (#660 G4, <c>docs/code-coverage/command-reactions-PRD.md</c>):
/// 👀 when its handler starts, then one current reaction for the command's state. Each state replaces the previous one by
/// its id, so a comment never carries two of the App's reactions.
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow seam of its own</b> (<see cref="EForgeCapability.CommentReactions"/>), like <see cref="IForgeLabels"/>: a
/// forge declaring the capability registers exactly one implementation for its provider, and the conformance test holds
/// both directions. GitHub is the only implementation; only GitHub raises comment commands (PRD §1b).
/// </para>
/// <para>
/// Measured on GitHub (spike S1): adding a reaction the App already placed answers 200 with the existing id, so a replayed
/// delivery cannot stack reactions; removing one that is gone answers 404, which <see cref="RemoveAsync"/> treats as done.
/// </para>
/// <para>
/// A missing credential or permission throws <see cref="ForgeAccessDeniedException"/> naming the permission. Callers react
/// best effort (R7): a reaction never fails the command it describes.
/// </para>
/// </remarks>
public interface IForgeCommentReactions
{
    EForgeProvider Provider { get; }

    /// <summary>Adds the App's <paramref name="reaction"/> to the comment; returns the reaction's id (the existing one when it is there already).</summary>
    Task<long> AddAsync(Repository repository, long commentId, EForgeReaction reaction, CancellationToken cancellationToken = default);

    /// <summary>Removes the App's reaction <paramref name="reactionId"/> from the comment. One that is gone already is no error.</summary>
    Task RemoveAsync(Repository repository, long commentId, long reactionId, CancellationToken cancellationToken = default);
}

/// <summary>The reactions the bot uses (#660 G2): what each means is the command lifecycle's.</summary>
public enum EForgeReaction
{
    /// <summary>👍: nothing changed.</summary>
    ThumbsUp,
    /// <summary>👎: refused or failed.</summary>
    ThumbsDown,
    /// <summary>👀: the bot is on it.</summary>
    Eyes,
    /// <summary>🚀: a long-running process started.</summary>
    Rocket,
    /// <summary>🎉: done.</summary>
    Hooray,
}
