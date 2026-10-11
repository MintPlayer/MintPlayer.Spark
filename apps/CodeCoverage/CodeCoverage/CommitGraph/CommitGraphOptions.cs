namespace CodeCoverage.CommitGraph;

/// <summary>
/// The <c>CommitGraph</c> configuration section (#504). Every number here is a placeholder from
/// the PRD (D7, D8, D10) until the VPS measurements replace it.
/// </summary>
public sealed class CommitGraphOptions
{
    public const string Section = "CommitGraph";

    /// <summary>
    /// The sidecar's base URL, <c>http://coverage-git:8080</c> in production. Unset means the
    /// feature is off: the endpoint answers <c>unavailable</c> / <c>disabled</c> and nothing is queued.
    /// </summary>
    public string? SidecarUrl { get; set; }

    /// <summary>Soft total of every mirror's size (D20): exceeded, least-recently-viewed mirrors are evicted. 20 GB.</summary>
    public long BudgetBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>The share of <see cref="BudgetBytes"/> one installation's mirrors may take (D10). A quarter.</summary>
    public double InstallationShare { get; set; } = 0.25;

    /// <summary>A mirror nobody has viewed for this many days is evicted by the daily job (D10).</summary>
    public int EvictAfterDays { get; set; } = 60;

    /// <summary>A mirror older than this when the graph is read is refetched in the background (D23).</summary>
    public int StaleAfterMinutes { get; set; } = 10;

    /// <summary>A refused or failed clone is not retried by reads for this long.</summary>
    public int RetryAfterMinutes { get; set; } = 60;

    /// <summary>Quotas for a repository cloned with a credential (D7).</summary>
    public CommitGraphQuota Installed { get; set; } = new() { MaxBytes = 2L * 1024 * 1024 * 1024, MaxRefs = 50_000, TimeoutSeconds = 600 };

    /// <summary>Quotas for a public repository cloned anonymously (D6, D7): stricter.</summary>
    public CommitGraphQuota Anonymous { get; set; } = new() { MaxBytes = 500L * 1024 * 1024, MaxRefs = 10_000, TimeoutSeconds = 180 };

    /// <summary>
    /// The key the author-email hash in avatar cache ids is made with (D28). When unset it is
    /// derived from <c>Coverage:BadgeSigningKey</c> under its own purpose string; when neither is
    /// set, avatars are off and every author gets initials. See <c>CommitGraphAvatars</c>.
    /// </summary>
    public string? AvatarKeySecret { get; set; }

    /// <summary>The largest avatar image fetched and cached. 256 KB; GitHub's are a few KB.</summary>
    public int AvatarMaxBytes { get; set; } = 256 * 1024;

    /// <summary>The per-installation cap in bytes.</summary>
    public long InstallationCapBytes => (long)(BudgetBytes * InstallationShare);
}

/// <summary>One quota tier, sent to the sidecar with every fetch.</summary>
public sealed class CommitGraphQuota
{
    public long MaxBytes { get; set; }
    public int MaxRefs { get; set; }
    public int TimeoutSeconds { get; set; }
}
