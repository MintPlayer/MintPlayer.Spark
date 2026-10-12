using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// The app's record of one repository's commit-graph mirror (#504, PRD §4.2): whether the sidecar
/// holds one, how its last fetch went, how big it is and when someone last looked at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive.</b> One small document per repository at an id derived from the repository's
/// (<see cref="ForRepository"/>), so it is loaded by id and never re-keys anything that exists.
/// The mirror itself lives in the <c>coverage-git</c> sidecar; this document is how the app decides
/// what to fetch, what to evict and what to tell the panel.
/// </para>
/// <para>
/// ⚠️ <b>The invariant every deletion path relies on: a mirror exists only while this document
/// does.</b> The endpoint creates it before it asks for the first clone, and deletion removes the
/// mirror first and this document after. A trigger that finds no document therefore has nothing to
/// delete and nothing to fetch.
/// </para>
/// <para>
/// <b>Indexed for one reader only:</b> the administrators' mirrors list (the <c>GetRepositoryGraphStates</c>
/// query, plan M5b). Every code path loads it by id or streams it by <see cref="Prefix"/>.
/// </para>
/// </remarks>
[GenerateIndex]
public class RepositoryGraphState
{
    public string? Id { get; set; }

    /// <summary>The repository document id, e.g. <c>Repositories/github/123</c>.</summary>
    [Reference(typeof(Repository))]
    public string Repository { get; set; } = string.Empty;

    /// <summary>True once a clone succeeded, until the mirror is deleted or found missing.</summary>
    public bool HasMirror { get; set; }

    /// <summary>When the last clone or fetch finished, whatever its outcome.</summary>
    public DateTime? LastFetchAtUtc { get; set; }

    /// <summary>When the graph was last read (D10). Eviction goes oldest first by this.</summary>
    public DateTime? LastViewedAtUtc { get; set; }

    /// <summary><c>cloned</c>, <c>fetched</c>, <c>refused</c> or <c>failed</c>, as the sidecar answered; null before the first fetch.</summary>
    public string? Outcome { get; set; }

    /// <summary>Why a fetch was refused or failed (the sidecar's reason, or the app's own such as <c>installation-cap</c>).</summary>
    public string? Reason { get; set; }

    /// <summary>The mirror's size on disk after its last fetch, as the sidecar measured it (D8).</summary>
    public long SizeBytes { get; set; }

    /// <summary>The sidecar's hash of the mirror's refs after the last fetch.</summary>
    public string? RefsHash { get; set; }

    /// <summary>Commits in the mirror after the last fetch.</summary>
    public long CommitCount { get; set; }

    /// <summary>
    /// What the credential the mirror was cloned with belongs to (on GitHub, <c>github:{installation}</c>),
    /// for the per-installation cap (D10). Null for an anonymous clone.
    /// </summary>
    public string? InstallationId { get; set; }

    /// <summary>A clone or fetch has been asked for and has not finished.</summary>
    public bool Syncing { get; set; }

    /// <summary>When <see cref="Syncing"/> was set; a request older than the fetch timeout is considered lost.</summary>
    public DateTime? SyncingSinceUtc { get; set; }

    /// <summary>The state id for a repository document id: <c>Repositories/github/1</c> becomes <c>RepositoryGraphStates/github/1</c>.</summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return Prefix + repositoryId[prefix.Length..];
    }

    /// <summary>Every state document's id starts with this, so they are listed by prefix with no index.</summary>
    public const string Prefix = "RepositoryGraphStates/";
}

/// <summary>Running totals of the mirror housekeeping, for the host metrics dashboard (D10, §9).</summary>
public class CommitGraphMetrics
{
    /// <summary>The single document's id.</summary>
    public const string DocumentId = "CommitGraphMetrics/mirrors";

    public string? Id { get; set; }

    /// <summary>Mirrors deleted to make room or because nobody looked at them (not deletions for lost access).</summary>
    public long Evictions { get; set; }

    /// <summary>Clones refused, by the sidecar's quotas or the app's per-installation cap.</summary>
    public long Refusals { get; set; }

    public DateTime? LastEvictionAtUtc { get; set; }
}

/// <summary>
/// A cached answer to "which avatar belongs to this commit author" (D28), positive or negative.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by a keyed hash of the email, never by the email.</b> The id is
/// <c>AuthorAvatars/{forge}/{hmac}</c>; the email itself is not stored anywhere, and the hash cannot
/// be reversed by a dictionary of addresses without the server's key, unlike a Gravatar hash.
/// </para>
/// <para>
/// Expires through <c>@expires</c>: seven days for a found avatar, one day for a miss. The image
/// bytes, once fetched, are an attachment on this document and expire with it.
/// </para>
/// </remarks>
public class AuthorAvatar
{
    public const string Prefix = "AuthorAvatars/";

    /// <summary>The attachment holding the fetched image.</summary>
    public const string ImageAttachment = "image";

    public string? Id { get; set; }

    /// <summary>Where the image is, or null for a negative entry (initials).</summary>
    public string? Url { get; set; }

    public DateTime ResolvedAtUtc { get; set; }
}
