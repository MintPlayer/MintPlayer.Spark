namespace CodeCoverage.Entities;

/// <summary>
/// The commit-level coverage record: the union of everything every finalized
/// build of the commit measured, plus files carried over from the base commit
/// when this commit's builds were partial and the file is byte-identical (same
/// git blob OID) at both ends. Document id is {commitId}/assembly; the
/// assembled per-file documents live under {commitId}/assembly/files/{hash}
/// and the tree summary at {commitId}/assembly/tree. Rebuilt from scratch on
/// every finalize of any build of the commit, so it is a pure function of the
/// builds and the base. Percentages are never stored, only counts.
/// </summary>
public class CommitAssembly
{
    public const string Complete = "Complete";
    public const string Partial = "Partial";

    public const string ReasonNoBase = "noBase";
    public const string ReasonBaseWalked = "baseWalked";
    public const string ReasonBaseMismatch = "baseMismatch";
    public const string ReasonNoFileList = "noFileList";
    public const string ReasonNoBlobIds = "noBlobIds";
    public const string ReasonTestsFailed = "testsFailed";
    public const string ReasonUnmeasuredChanges = "unmeasuredChanges";

    public string? Id { get; set; }

    public string? Commit { get; set; }

    public string? Repository { get; set; }

    public string Sha { get; set; } = string.Empty;

    /// <summary>The builds whose measured files were assembled — the highest attempt of every finalized run.</summary>
    public List<AssemblyBuild> Builds { get; set; } = [];

    /// <summary>The base sha the contributing builds declared (first finalized wins), or null.</summary>
    public string? BaseRequestedSha { get; set; }

    /// <summary>The commit files were actually carried from, or null when nothing could be.</summary>
    public string? BaseSha { get; set; }

    /// <summary>How the base was found: exact | mergeBase | walked | none.</summary>
    public string? BaseResolution { get; set; }

    public int HeadFileCount { get; set; }

    public bool HeadHasOids { get; set; }

    public int MeasuredFiles { get; set; }

    public int CarriedFiles { get; set; }

    /// <summary>
    /// Files the base knew that changed since (OID differs) and no build of this
    /// commit re-measured — the only files whose absence makes the number wrong.
    /// Files the base never knew about are not counted: a new file always lives
    /// in a project that is affected by definition.
    /// </summary>
    public int UnmeasuredFiles { get; set; }

    public CoverageSummary Coverage { get; set; } = new();

    /// <summary><see cref="Complete"/> or <see cref="Partial"/>.</summary>
    public string Completeness { get; set; } = Partial;

    public List<string> IncompleteReasons { get; set; } = [];

    /// <summary>Of all carried files, the sha the oldest one was originally measured at; null when nothing was carried.</summary>
    public string? OldestOriginSha { get; set; }

    public DateTime AssembledAtUtc { get; set; }

    public static string DocumentId(string commitId) => $"{commitId}/assembly";

    public static string FilesPrefix(string commitId) => $"{commitId}/assembly/files/";

    public static string FileDocumentId(string commitId, string normalizedPath)
        => $"{commitId}/assembly/files/{FileCoverage.PathHash(normalizedPath)}";

    public static string TreeDocumentId(string commitId) => $"{commitId}/assembly/tree";
}

public class AssemblyBuild
{
    public string BuildId { get; set; } = string.Empty;
    public long CiRunId { get; set; }
    public int CiRunAttempt { get; set; }
    public bool Partial { get; set; }
    public bool CarryForward { get; set; }
    public string? DeclaredBaseSha { get; set; }
}

/// <summary>Where an assembled file's coverage came from.</summary>
public class FileOrigin
{
    public const string Measured = "Measured";
    public const string Carried = "Carried";

    /// <summary><see cref="Measured"/> on this commit, or <see cref="Carried"/> from the base.</summary>
    public string Kind { get; set; } = Measured;

    /// <summary>The commit this copy was taken from: the commit itself when measured, the base when carried.</summary>
    public string? FromSha { get; set; }

    /// <summary>The build that measured it (on this commit or, when carried, on some earlier one).</summary>
    public string? FromBuildId { get; set; }

    /// <summary>The commit at which the file was last actually measured — survives any number of carry hops.</summary>
    public string? OriginSha { get; set; }
}
