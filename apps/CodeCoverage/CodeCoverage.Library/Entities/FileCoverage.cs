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
    /// <summary>Document id of this file's coverage, <c>{buildId}/files/{pathHash}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>Document id of the build this file's coverage was merged for.</summary>
    public string BuildId { get; set; } = string.Empty;

    /// <summary>Normalized repo-relative path with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>False when the path couldn't be matched to the repo file list.</summary>
    public bool Matched { get; set; } = true;

    /// <summary>
    /// The path exactly as the report supplied it, kept only when it differs from
    /// <see cref="Path"/> — so an unmatched file can be diagnosed without re-running
    /// the upload (#417). Issue #415 cost a day partly because the server's view of
    /// what the client actually sent was unrecoverable after normalisation.
    ///
    /// Diagnostic only. <see cref="Path"/> remains the stored path and the hash
    /// identity: <see cref="DocumentId"/> derives from it, so this is an addition
    /// and never a substitute.
    /// </summary>
    public string? RawPath { get; set; }

    /// <summary>
    /// Git blob OID of <see cref="Path"/> at the measured commit, taken from the
    /// uploader's file list when it carried OIDs and the path matched. Null for
    /// unmatched paths and for uploads from action builds that sent bare paths.
    /// Carry-forward copies a file into a later commit's assembly only when
    /// this equals the later commit's OID for the same path.
    /// </summary>
    public string? BlobOid { get; set; }

    /// <summary>
    /// Set only on assembled copies ({commitId}/assembly/files/…): whether this
    /// commit measured the file or carried it from the base, and from where.
    /// Null on the per-build documents, which are always measured by definition.
    /// </summary>
    public FileOrigin? Origin { get; set; }

    /// <summary>Per-line coverage status for every coverable line; non-coverable lines are absent.</summary>
    public List<LineCoverage> Lines { get; set; } = [];

    /// <summary>Per-line branch coverage, present only for formats that report branches.</summary>
    public List<LineBranchCoverage> Branches { get; set; } = [];

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
    /// <summary>One-based line number within the source file.</summary>
    public int Number { get; set; }

    /// <summary>Execution count; null when the source format has none (e.g. JaCoCo).</summary>
    public int? Hits { get; set; }

    /// <summary>Whether the line was not covered, partially covered (some branches missed) or fully covered.</summary>
    public LineStatus Status { get; set; }
}

/// <summary>
/// One line's branch coverage, stored in the only shape that merges correctly
/// across report formats.
/// <para>
/// Coverage report formats fall into two groups. Some identify each arm of a
/// branching expression (lcov's block/branch ordinals, istanbul's branchMap key
/// plus arm index, Cobertura's &lt;condition number=&gt;); the rest report a bare
/// count of how many arms were taken without saying which (Cobertura's
/// condition-coverage, JaCoCo's mb/cb, Clover's truecount/falsecount).
/// </para>
/// <para>
/// So a line keeps <see cref="TakenArms"/> — the union of arms observed taken,
/// meaningful only across identity-carrying reports — and <see cref="Floor"/>,
/// the strongest "at least this many were taken" claim from a count-only report.
/// <c>Covered = max(Floor, TakenArms.Count)</c> and <c>Total = Arity</c>. Union
/// and max are both commutative and associative, so merging reports is
/// order-independent by construction: the same set of uploads always yields the
/// same stored document, whatever order they arrive in.
/// </para>
/// <para>
/// A flat list of edges cannot do this, which is what the previous model got
/// wrong. Cobertura and JaCoCo synthesize positional edge ids ("0"/0, "0"/1)
/// that are indistinguishable from lcov's real ones but carry no meaning, so
/// unioning across formats would claim arms that were never covered — the
/// reason the old code discarded foreign-format branches outright instead.
/// </para>
/// </summary>
public class LineBranchCoverage
{
    /// <summary>One-based line number the branching expression sits on.</summary>
    public int Line { get; set; }

    /// <summary>How many arms this line has — the largest arity any report claimed.</summary>
    public int Arity { get; set; }

    /// <summary>
    /// Arm keys observed taken, from identity-carrying reports only. Stored
    /// sorted so two documents merged from the same reports in different orders
    /// are byte-identical.
    /// </summary>
    public List<string> TakenArms { get; set; } = [];

    /// <summary>
    /// The largest count-only "at least this many arms were taken" claim. Never
    /// added to <see cref="TakenArms"/> — both describe the same arms, one by
    /// name and one by number.
    /// </summary>
    public int Floor { get; set; }

    /// <summary>Arms known to be taken: the count-only floor or the named set, whichever is larger.</summary>
    public int Covered => Math.Max(Floor, TakenArms.Count);

    /// <summary>A line is partial when it was executed but some arm was never taken.</summary>
    public bool IsPartial => Covered < Arity;
}
