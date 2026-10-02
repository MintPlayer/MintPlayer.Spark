using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>
/// A veto over documents an interceptor writes on the caller's behalf that are <b>not</b> persistent-object
/// saves — a contribution, say, written while the caller saves its target. Interceptors only see PO
/// saves, so a library that refuses saves (Moderation: suspended accounts, locked posts) would never
/// see these writes; the library that makes them asks every registered guard first.
/// </summary>
/// <remarks>
/// Optional by design: a library that writes satellite documents resolves
/// <c>IEnumerable&lt;ISatelliteWriteGuard&gt;</c> and calls each; with none registered nothing is
/// checked. Neither side references the other — both reference only this contract. A guard refuses by
/// throwing, exactly as an interceptor's before-hook does (<see cref="SparkValidationException"/> for a
/// 400). Not called for the system context.
/// </remarks>
public interface ISatelliteWriteGuard
{
    /// <summary>Throws when the caller may not write <see cref="SatelliteWriteContext.DocumentId"/>.</summary>
    ValueTask EnsureMayWriteAsync(SatelliteWriteContext context);
}

/// <summary>One satellite document about to be written (created, changed, withdrawn or deleted).</summary>
public sealed class SatelliteWriteContext
{
    /// <summary>The CLR type of the document written (<c>SongLyricsContribution</c>).</summary>
    public required Type DocumentType { get; init; }

    /// <summary>The document id written.</summary>
    public required string DocumentId { get; init; }

    /// <summary>The entity the document belongs to (<c>Song</c>).</summary>
    public required Type TargetType { get; init; }

    /// <summary>The id of the entity the document belongs to.</summary>
    public required string TargetId { get; init; }

    /// <summary>The caller.</summary>
    public ClaimsPrincipal? User { get; init; }
}
