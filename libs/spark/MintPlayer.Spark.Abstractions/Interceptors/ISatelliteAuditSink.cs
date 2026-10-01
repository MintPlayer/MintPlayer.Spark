using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>
/// Records a moderator's action on satellite documents (documents written on the caller's behalf that
/// are not persistent-object saves) in an audit log — a removed contribution version, a revert
/// (contributions M5b). The counterpart of <see cref="ISatelliteWriteGuard"/>.
/// </summary>
/// <remarks>
/// Optional by design: the library that acts resolves <c>IEnumerable&lt;ISatelliteAuditSink&gt;</c>
/// and calls each after its write committed; with none registered nothing is recorded. Neither side
/// references the other. Moderation registers one that writes its append-only audit entries.
/// </remarks>
public interface ISatelliteAuditSink
{
    /// <summary>Records <paramref name="entry"/>. Called after the action's commit.</summary>
    ValueTask RecordAsync(SatelliteAuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>One audited action over satellite documents.</summary>
public sealed class SatelliteAuditEntry
{
    /// <summary>What was done, as a stable verb (<c>RevertContribution</c>, <c>RemoveContributionVersion</c>).</summary>
    public required string Action { get; init; }

    /// <summary>The CLR type of the documents acted on (<c>SongLyricsContribution</c>).</summary>
    public required Type DocumentType { get; init; }

    /// <summary>The document the action was about (the contribution reverted to, the removed current document).</summary>
    public required string DocumentId { get; init; }

    /// <summary>Every document the action changed (hidden or deleted), in id order.</summary>
    public required IReadOnlyList<string> AffectedDocumentIds { get; init; }

    /// <summary>The entity the documents belong to (<c>Song</c>).</summary>
    public required Type TargetType { get; init; }

    /// <summary>The id of that entity.</summary>
    public required string TargetId { get; init; }

    /// <summary>The reason written on the hidden documents (<c>reverted</c>, <c>version-removed</c>).</summary>
    public string? Reason { get; init; }

    /// <summary>The caller.</summary>
    public ClaimsPrincipal? User { get; init; }
}
