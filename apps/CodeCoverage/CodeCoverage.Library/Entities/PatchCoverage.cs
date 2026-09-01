namespace CodeCoverage.Entities;

/// <summary>
/// Coverage of the lines a PR added, computed at finalize from the head
/// build's per-line FileCoverage and GitHub's three-dot diff — never from the
/// base's stored coverage, so it works even when the base has none. Diff files
/// the build didn't measure are skipped, not zeroed: under nx-affected uploads
/// an unaffected project's changed lines must not read as misses.
/// </summary>
public class PatchCoverage
{
    /// <summary>What the diff compared against (declared base sha, or the PR base tip hint).</summary>
    public string? DiffBaseRef { get; set; }

    /// <summary>The merge-base GitHub computed for that comparison.</summary>
    public string? MergeBaseSha { get; set; }

    public int LinesCovered { get; set; }

    public int LinesCoverable { get; set; }

    public int FilesInDiff { get; set; }

    /// <summary>Diff files the build actually measured — the denominator's file scope.</summary>
    public int FilesMatched { get; set; }

    /// <summary>True when GitHub's 300-file comparison cap cut the diff short; the number under-reports.</summary>
    public bool DiffTruncated { get; set; }
}
