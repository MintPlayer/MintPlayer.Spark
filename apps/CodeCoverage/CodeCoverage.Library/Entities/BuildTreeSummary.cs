namespace CodeCoverage.Entities;

/// <summary>
/// Per-file line totals of one Build, materialized at finalize so the tree
/// and hierarchy endpoints read one small document instead of re-streaming
/// every FileCoverage (full line arrays) on each request. Document id is
/// {buildId}/tree; re-finalizing overwrites it. Not exposed through Spark's
/// generic UI.
/// </summary>
public class BuildTreeSummary
{
    /// <summary>Document id of this summary, <c>{buildId}/tree</c>.</summary>
    public string? Id { get; set; }

    /// <summary>Document id of the build whose per-file totals this summary materializes.</summary>
    public string BuildId { get; set; } = string.Empty;

    /// <summary>One entry per measured file with its covered and coverable line counts, from which the file tree is rendered.</summary>
    public List<TreeFileSummary> Files { get; set; } = [];

    public static string DocumentId(string buildId) => $"{buildId}/tree";

    /// <summary>Per-flag tree, from that flag's merged file documents only.</summary>
    public static string FlagDocumentId(string buildId, string flag)
        => $"{buildId}/flags/{FileCoverage.SanitizeFlag(flag)}/tree";
}

public class TreeFileSummary
{
    /// <summary>Normalized repo-relative path with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>False when the path couldn't be matched to the repo file list.</summary>
    public bool Matched { get; set; } = true;

    /// <summary>Number of coverable lines in this file that were executed at least once.</summary>
    public int LinesCovered { get; set; }

    /// <summary>Number of lines in this file that tests could have executed.</summary>
    public int LinesCoverable { get; set; }

    /// <summary>On assembled trees: <see cref="FileOrigin.Measured"/> or <see cref="FileOrigin.Carried"/>. Null on per-build trees.</summary>
    public string? Origin { get; set; }

    /// <summary>On assembled trees, for carried files: the commit the file was carried from.</summary>
    public string? CarriedFromSha { get; set; }
}
