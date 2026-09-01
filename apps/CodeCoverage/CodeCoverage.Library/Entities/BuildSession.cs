namespace CodeCoverage.Entities;

/// <summary>One upload (one action invocation) within a Build.</summary>
public class BuildSession
{
    public string SessionId { get; set; } = string.Empty;

    public string? JobName { get; set; }

    public string[] Flags { get; set; } = [];

    public DateTime UploadedAtUtc { get; set; }

    /// <summary>"Pending" | "Parsed" | "Failed"</summary>
    public string ParseStatus { get; set; } = "Pending";

    public string? Error { get; set; }

    /// <summary>Attachment names on the Build document holding this session's raw report files.</summary>
    public string[] RawFileNames { get; set; } = [];

    /// <summary>The CI workspace root (GITHUB_WORKSPACE), for stripping absolute report paths.</summary>
    public string? RootDir { get; set; }

    public int FilesCount { get; set; }
}
