namespace CodeCoverage.Entities;

/// <summary>One upload (one action invocation) within a Build.</summary>
public class BuildSession
{
    /// <summary>Unique id of this upload within the build, assigned when the action's report files are received.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Name of the CI job that produced this upload, as reported by the action.</summary>
    public string? JobName { get; set; }

    /// <summary>Flags the uploader tagged this session with (e.g. a project or test suite name); each flag gets its own per-flag coverage totals.</summary>
    public string[] Flags { get; set; } = [];

    /// <summary>When this session's report files were received (UTC).</summary>
    public DateTime UploadedAtUtc { get; set; }

    /// <summary>"Pending" | "Parsed" | "Failed"</summary>
    public string ParseStatus { get; set; } = "Pending";

    /// <summary>Why parsing this session's reports failed; null unless the parse status is <c>Failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>Attachment names on the Build document holding this session's raw report files.</summary>
    public string[] RawFileNames { get; set; } = [];

    /// <summary>The CI workspace root (GITHUB_WORKSPACE), for stripping absolute report paths.</summary>
    public string? RootDir { get; set; }

    /// <summary>Number of source files this session's reports contained coverage for.</summary>
    public int FilesCount { get; set; }
}
