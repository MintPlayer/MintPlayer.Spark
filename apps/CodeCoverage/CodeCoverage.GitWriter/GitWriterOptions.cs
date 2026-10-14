using CodeCoverage.Git;

namespace CodeCoverage.GitWriter;

/// <summary>
/// The writer's configuration, section <c>GitWriter</c> (environment: <c>GitWriter__Root</c>, …). The job caps come from
/// spike S8 (docs/coverage_merge_queue_PRD.md §7): a prepare on this repository peaked at 100 MB and 15.7 MB on disk, so a
/// job gets 512 MB of memory, 64 processes and 1 GB of disk. Memory and processes are the container's limits
/// (docker-compose <c>mem_limit</c>/<c>pids_limit</c>), which equal a job's because only <see cref="MaxConcurrentJobs"/>
/// jobs run at once; disk and wall-clock time are enforced here.
/// </summary>
public sealed class GitWriterOptions
{
    public const string Section = "GitWriter";

    /// <summary>The jobs' volume. Each job works in <c>{Root}/{job id}</c>, deleted when the job ends.</summary>
    public string Root { get; set; } = "/data/jobs";

    /// <summary>The host free-space floor: below it a job is refused with <c>disk-floor</c> before git starts.</summary>
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>A job's disk cap (S8: 1 GB). A watchdog measures the job directory while the job runs and stops it, process
    /// tree included, as soon as it is over (<c>too-large</c>).</summary>
    public long MaxJobBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>A job's wall clock; when it passes, the running git process tree is killed (<c>timeout</c>). Well under
    /// the messaging ClaimTtl of 5 minutes, so the saga's claim outlives the job (§5.5, S8).</summary>
    public int JobTimeoutSeconds { get; set; } = 180;

    /// <summary>Jobs that run at once: 1 or 2. The container's memory and pids limits are sized for one job (S8); raising
    /// this to 2 needs them raised too (docs/code-coverage/hosting.md).</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>How long a request waits for a free slot before it is answered <c>busy</c> (the saga retries).</summary>
    public int SlotWaitSeconds { get; set; } = 30;

    /// <summary>The largest regenerate patch accepted (T4). Spark's generated files change by a few KB per entity.</summary>
    public long MaxPatchBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>An empty, read-only directory every git process gets as <c>core.hooksPath</c>. The image creates
    /// <c>/opt/git-hooks-empty</c> owned by root; when empty, one is created under the temp directory.</summary>
    public string? HooksPath { get; set; }

    /// <summary>Test seam only: accept <c>http://</c> remotes (the tests' local git server has no TLS). Production accepts
    /// https only.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>The shared runner's settings: <c>transfer.fsckObjects</c> ON (T8), because the writer builds commits from
    /// what it fetched. The read-only mirror keeps it off (D22).</summary>
    public GitRunnerOptions ToRunnerOptions() => new() { HooksPath = HooksPath, AllowInsecureHttp = AllowInsecureHttp, CheckReceivedObjects = true };
}
