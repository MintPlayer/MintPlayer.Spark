namespace CodeCoverage.Entities;

/// <summary>
/// A stored resource whose reachability on its forge we track.
/// </summary>
/// <remarks>
/// <para>
/// The three fields move together or not at all: a <see cref="RepositoryConnection.Disconnected"/>
/// with no reason cannot be explained to its owner, and a
/// <see cref="RepositoryConnection.Connected"/> that kept a stale reason reads, everywhere the
/// reason is shown, as a repository that is both fine and broken. Keeping them behind
/// <see cref="ForgeConnectionState"/> is what makes "together" enforceable rather than remembered.
/// </para>
/// <para>
/// ⚠️ This interface exists because the write was <b>duplicated five times</b> — twice for
/// <c>Repository</c> and once for <c>GitHubProject</c> as private helpers in two
/// <c>CodeCoverage.GithubIntegration</c> classes, plus once inline in the upload controller. Every
/// one of those copies lived in GitHub-specific code, so a second forge had no way to write
/// connection state except to write a sixth copy. That is the gap M8 recorded; this is the neutral
/// write path it was missing.
/// </para>
/// </remarks>
public interface IForgeConnectable
{
    /// <summary>Whether we can still act on the resource.</summary>
    RepositoryConnection Connection { get; set; }

    /// <summary>Why not, from <see cref="DisconnectedReasons"/>; null while connected.</summary>
    string? DisconnectedReason { get; set; }

    /// <summary>When it was last disconnected (UTC); null while connected.</summary>
    DateTime? DisconnectedAtUtc { get; set; }
}

/// <summary>
/// The only supported way to write connection state, for any forge.
/// </summary>
public static class ForgeConnectionState
{
    /// <summary>
    /// Marks the resource reachable again, clearing any record of why it was not.
    /// </summary>
    /// <remarks>
    /// Clearing the reason is the point, not tidiness: the reason is what the owner is shown, and a
    /// reconnected repository still carrying <c>AppUninstalled</c> tells them to reinstall an App
    /// that is already installed.
    /// </remarks>
    public static void MarkConnected(this IForgeConnectable connectable)
    {
        ArgumentNullException.ThrowIfNull(connectable);

        connectable.Connection = RepositoryConnection.Connected;
        connectable.DisconnectedReason = null;
        connectable.DisconnectedAtUtc = null;
    }

    /// <summary>
    /// Marks the resource unreachable, recording why and when.
    /// </summary>
    /// <remarks>
    /// Never deletes anything: the coverage history stays, the badge keeps serving its last known
    /// value, and the report URLs keep resolving — the resource simply stops being advertised to
    /// anyone but its owner.
    /// <para>
    /// ⚠️ <paramref name="reason"/> must come from <see cref="DisconnectedReasons"/>. It is stored
    /// verbatim and read back by code that compares strings, so an ad-hoc value is a silent
    /// mismatch rather than an error — which is why this refuses an empty one outright.
    /// </para>
    /// </remarks>
    public static void MarkDisconnected(this IForgeConnectable connectable, string reason)
    {
        ArgumentNullException.ThrowIfNull(connectable);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        connectable.Connection = RepositoryConnection.Disconnected;
        connectable.DisconnectedReason = reason;
        connectable.DisconnectedAtUtc = DateTime.UtcNow;
    }
}
