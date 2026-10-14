using CodeCoverage.Git;

namespace CodeCoverage.GitMirror;

/// <summary>
/// The sidecar's configuration, section <c>GitMirror</c> (environment: <c>GitMirror__Root</c>, …).
/// Quotas are not configured here: the app sends them with every fetch (D7), since it knows whether a repository is
/// installed or anonymous.
/// </summary>
public sealed class GitMirrorOptions
{
    public const string Section = "GitMirror";

    /// <summary>The mirrors' volume. A mirror lives at <c>{Root}/{provider}/{repoId}.git</c>.</summary>
    public string Root { get; set; } = "/data/git";

    /// <summary>The host free-space floor (D20, hard): below it every clone and fetch is refused with <c>disk-floor</c>.
    /// Placeholder 15 GB until S2's numbers are applied (D8).</summary>
    public long MinFreeBytes { get; set; } = 15L * 1024 * 1024 * 1024;

    /// <summary>The snapshot cache's byte budget (D22). Subjects and author text are counted; they are about 90% of a
    /// snapshot (D27).</summary>
    public long SnapshotCacheBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>An empty, read-only directory every git process gets as <c>core.hooksPath</c> (D5). The image creates
    /// <c>/opt/git-hooks-empty</c> owned by root; when empty, one is created under the temp directory.</summary>
    public string? HooksPath { get; set; }

    /// <summary>An open <c>git log</c> reader idle this long is closed; the snapshot resumes with <c>--skip</c>.</summary>
    public int ReaderIdleSeconds { get; set; } = 60;

    /// <summary>Test seam only: accept <c>http://</c> clone URLs (the tests' local git server has no TLS). Production
    /// accepts https only.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>The shared runner's settings (CodeCoverage.Git). The mirror keeps <c>transfer.fsckObjects</c> off, as it
    /// was before the runner moved: a treeless partial clone's promisor packs are its whole content.</summary>
    public GitRunnerOptions ToRunnerOptions() => new() { HooksPath = HooksPath, AllowInsecureHttp = AllowInsecureHttp };
}
