using System.Security.Cryptography;
using System.Text;

namespace CodeCoverage.Entities;

/// <summary>
/// Merged per-file coverage for one Build (max across its sessions).
/// Document id is {buildId}/files/{pathHash} so re-parsing a session
/// overwrites deterministically. Not exposed through Spark's generic UI.
/// </summary>
public class FileCoverage
{
    public string? Id { get; set; }

    public string BuildId { get; set; } = string.Empty;

    /// <summary>Normalized repo-relative path with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>False when the path couldn't be matched to the repo file list.</summary>
    public bool Matched { get; set; } = true;

    /// <summary>
    /// The report format (parser FormatName) that produced Branches. Branch
    /// identity schemes differ per format (lcov reports real block/branch ids;
    /// Cobertura and JaCoCo synthesize edges), so branch detail only merges
    /// within one format — a session in another format merges line status only.
    /// </summary>
    public string? BranchFormat { get; set; }

    /// <summary>
    /// Git blob OID of <see cref="Path"/> at the measured commit, taken from the
    /// uploader's file list when it carried OIDs and the path matched. Null for
    /// unmatched paths and for uploads from action builds that sent bare paths.
    /// Carry-forward copies a file into a later commit's assembly only when
    /// this equals the later commit's OID for the same path.
    /// </summary>
    public string? BlobOid { get; set; }

    public List<LineCoverage> Lines { get; set; } = [];

    public List<BranchCoverage> Branches { get; set; } = [];

    public static string DocumentId(string buildId, string normalizedPath)
        => $"{buildId}/files/{PathHash(normalizedPath)}";

    /// <summary>
    /// Per-flag merged copy of one file's coverage: sessions carrying the flag
    /// max-merge in here exactly as they do into the build-level document, so
    /// per-flag numbers survive retries the same way. Flag names are sanitized
    /// because they come off an upload form and become document-id segments.
    /// </summary>
    public static string FlagDocumentId(string buildId, string flag, string normalizedPath)
        => $"{buildId}/flags/{SanitizeFlag(flag)}/files/{PathHash(normalizedPath)}";

    public static string SanitizeFlag(string flag)
        => new([.. flag.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]);

    public static string PathHash(string normalizedPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexStringLower(bytes)[..20];
    }
}

public class LineCoverage
{
    public int Number { get; set; }

    /// <summary>Execution count; null when the source format has none (e.g. JaCoCo).</summary>
    public int? Hits { get; set; }

    public LineStatus Status { get; set; }
}

public class BranchCoverage
{
    public int Line { get; set; }

    /// <summary>Branching location within the line (format-specific block id).</summary>
    public string BlockId { get; set; } = string.Empty;

    /// <summary>One edge of the branching expression.</summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>Times this edge was taken; null when the enclosing block never executed.</summary>
    public int? Taken { get; set; }
}
