namespace CodeCoverage.Git;

/// <summary>How a <see cref="GitRunner"/> hardens the processes it starts. Each sidecar maps its own configuration
/// section onto this (GitMirror: <c>GitMirror</c>; the merge-queue writer: its own).</summary>
public sealed class GitRunnerOptions
{
    /// <summary>An empty, read-only directory every git process gets as <c>core.hooksPath</c>
    /// (docs/coverage_commit_graph_PRD.md D5). The images create <c>/opt/git-hooks-empty</c> owned by root; when empty,
    /// one is created under the temp directory.</summary>
    public string? HooksPath { get; set; }

    /// <summary>Test seam only: accept <c>http://</c> remotes (the tests' local git server has no TLS). Production
    /// accepts https only.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary><c>transfer.fsckObjects=true</c>: every object received by a fetch or clone is checked before it is
    /// stored (docs/coverage_merge_queue_PRD.md §4.3, T8). On for the merge-queue writer, which builds commits from what
    /// it fetched. Off for the read-only treeless mirror, whose behaviour is unchanged by the move into this library.</summary>
    public bool CheckReceivedObjects { get; set; }
}
