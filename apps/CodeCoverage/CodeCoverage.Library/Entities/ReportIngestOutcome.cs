using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// What happened to one uploaded report file. Issue #417: an upload that the server
/// cannot use must never present as a successful build, and a rejection must name
/// <i>which</i> file and <i>why</i> rather than failing the batch anonymously.
///
/// Recorded per attachment so that one bad report does not discard the good ones —
/// before this existed, a parser throw escaped the attachment loop and failed the
/// whole session, losing every report that had already merged.
/// </summary>
[ValueObject]
public partial class ReportIngestOutcome
{
    /// <summary>The uploaded file's name, as the consumer would recognise it.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>True when the file was parsed and its files merged.</summary>
    public bool Parsed { get; set; }

    /// <summary>The report format that claimed the file, when one did.</summary>
    public string? Format { get; set; }

    /// <summary>Files the report described, when it parsed.</summary>
    public int FilesCount { get; set; }

    /// <summary>
    /// Why the file was rejected, from <see cref="ReportRejectionReason"/>. Null when
    /// <see cref="Parsed"/>. A closed set so a consumer can branch on it.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Human-readable detail — the parser's own message, bounded.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// The closed set of rejection reasons. Values are added, never repurposed — the
/// action and any direct API consumer branch on them.
/// </summary>
public static class ReportRejectionReason
{
    /// <summary>The upload carried no bytes, or only whitespace.</summary>
    public const string Empty = "empty";

    /// <summary>No parser recognised the format. Clover and Istanbul JSON land here.</summary>
    public const string UnrecognizedFormat = "unrecognizedFormat";

    /// <summary>Recognised, but not well-formed — the generic parse failure.</summary>
    public const string Malformed = "malformed";

    /// <summary>Well-formed as far as it goes, but the document ends mid-element.</summary>
    public const string Truncated = "truncated";

    /// <summary>Exceeded the decompressed-size or document bound.</summary>
    public const string TooLarge = "tooLarge";

    /// <summary>Parsed cleanly but described no files at all.</summary>
    public const string NoFiles = "noFiles";

    /// <summary>The attachment named by the session was not present on the build.</summary>
    public const string Missing = "missing";
}
