namespace QnA.Services;

/// <summary>
/// Carries a state change that the client may never post (<c>IsClosed</c> is a read-only attribute)
/// from a custom action into the save it makes. The action records the change here, then saves the
/// question through <c>IDatabaseAccess</c>, so the change passes every gate an edit passes — the
/// <c>Edit</c> right, the author-or-moderator row rule, Moderation's lock, History's stamping and a
/// revision — and <see cref="Actions.QuestionActions"/> applies it in <c>OnBeforeSaveAsync</c>.
/// </summary>
/// <remarks>
/// Scoped: the change lives for the one request that asked for it. The entity mapper writes only
/// writable model attributes, which is why the action cannot simply set the value on the object.
/// </remarks>
public sealed class QuestionStateChanges
{
    private readonly Dictionary<string, bool> closed = new(StringComparer.Ordinal);

    /// <summary>Asks the next save of <paramref name="questionId"/> in this request to set <c>IsClosed</c>.</summary>
    public void SetClosed(string questionId, bool isClosed) => closed[questionId] = isClosed;

    /// <summary>Takes the pending <c>IsClosed</c> change of <paramref name="questionId"/>, if any.</summary>
    public bool TryTakeClosed(string questionId, out bool isClosed) => closed.Remove(questionId, out isClosed);
}
