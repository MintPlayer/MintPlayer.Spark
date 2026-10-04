using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

/// <summary>The body of <c>POST /spark/po/create</c> and <c>POST /spark/po/update</c>.</summary>
internal sealed class PersistentObjectRequest : ISparkTypedRequest, IRetryableRequest
{
    /// <inheritdoc />
    public string? ObjectTypeId { get; set; }

    /// <summary>
    /// The object to save on <c>update</c>; ignored on <c>create</c>, which forces a new id.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately a top-level field rather than <c>PersistentObject.Id</c>. Both name the target,
    /// but this one is the request saying what to act on, where the nested one is part of the submitted
    /// document — the same distinction as <see cref="ObjectTypeId"/> and
    /// <c>PersistentObject.ObjectTypeId</c>, and it is resolved the same way: the server loads this id,
    /// and overwrites the nested one with what it loaded.
    /// </remarks>
    public string? Id { get; set; }

    public Abstractions.PersistentObject? PersistentObject { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }

    /// <summary>
    /// Create only: the parent the New was started from, with <see cref="ParentType"/> and
    /// <see cref="QueryId"/> — the same three fields <c>POST /po/new</c> takes. Resolved and authorized
    /// server-side (<c>SubQueryNewParent</c>) and handed to the save hooks as
    /// <see cref="Abstractions.PersistentObject.Parent"/>.
    /// </summary>
    public string? ParentId { get; set; }

    /// <inheritdoc cref="ParentId"/>
    public string? ParentType { get; set; }

    /// <inheritdoc cref="ParentId"/>
    public string? QueryId { get; set; }
}

///<summary>The body of <c>POST /spark/po/load</c> and <c>POST /spark/po/delete</c>.</summary>
/// <remarks>
/// Carries no <c>PersistentObject</c>: a load has nothing to submit, and a delete names its target by
/// id. Both are retryable — a load can prompt from <c>OnLoadAsync</c>, which is what having a body at
/// all is what makes possible.
/// </remarks>
internal sealed class PersistentObjectReferenceRequest : ISparkTypedRequest, IRetryableRequest
{
    /// <inheritdoc />
    public string? ObjectTypeId { get; set; }

    /// <summary>The object's id. Raven ids contain slashes, which is why this never sat in a route.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// <c>load</c> only (ignored by <c>delete</c>): whether a deleted row may be returned —
    /// <c>exclude</c> (default), <c>include</c> or <c>only</c> (#460, T2 on the load side, so a row
    /// can be opened from the recycle bin). Carried to row policies through
    /// <c>RowPolicyContext.Deleted</c>; the SoftDelete package honours it only for holders of
    /// <c>ViewDeleted/T</c>, exactly as on a query. Core itself filters nothing on it.
    /// </summary>
    public SparkDeletedFilter? Deleted { get; set; }

    /// <summary>
    /// <c>delete</c> only (ignored by <c>load</c>), and required there (#467, D14): the version of the
    /// row the caller saw. A delete without one is a 400; a row changed since is a 409.
    /// </summary>
    public string? Etag { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }
}
