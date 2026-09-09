using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// An upload credential for CI. The plaintext value is shown once at creation and never stored;
/// only its SHA-256 is kept, in <see cref="Hash"/>.
///
/// Deliberately app-local: the planned extraction into a generic Spark
/// ApiTokens library was cancelled upstream in favor of client_credentials
/// (docs/PRD.md §10) — Coverage keeps its own covt_ tokens.
/// </summary>
/// <remarks>
/// ⚠️ <b>The document id used to be the hash</b> — <c>ApiTokens/{sha256}</c> — which made lookup a
/// point-load and uniqueness free. It is a guid now, because this type became a Spark persistent
/// object and a PersistentObject always carries its id on the wire. With the hash as the id, every
/// row a caller could read shipped that hash to the browser; with a guid, the hash never leaves the
/// server, since <see cref="Hash"/> is deliberately absent from <c>ApiToken.json</c> and Spark only
/// projects declared attributes.
/// <para>
/// Never shipping a value is a stronger guarantee than shipping it only to the right people, which
/// depends on a row filter staying correct forever. The cost is that authentication is now an
/// indexed lookup rather than a point-load.
/// </para>
/// <para>
/// A leaked hash is not a usable credential either way — the token is 32 bytes from
/// <c>RandomNumberGenerator</c>, so there is no feasible preimage — but disclosure of which tokens
/// exist, for whom, and when has no upside.
/// </para>
/// </remarks>
public class ApiToken
{
    /// <summary>Document id, <c>ApiTokens/{guid}</c>. Carries no information — see the remarks above.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// SHA-256 of the token value, lowercase hex. The only thing that identifies a presented token,
    /// and the field authentication looks up.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>[IgnoreProperty]</c> is what keeps it off the wire, and it is not optional.</b>
    /// Spark projects exactly the attributes the model declares, and model synchronization
    /// <em>discovers</em> public properties — so simply omitting it from <c>ApiToken.json</c> does
    /// not work: the next synchronize run puts it straight back. The attribute is the instruction;
    /// the absent JSON block is only its effect.
    /// <para>
    /// RavenDB still persists it: the attribute excludes the property from the Spark model, not from
    /// the document.
    /// </para>
    /// </remarks>
    [IgnoreProperty]
    public string Hash { get; set; } = string.Empty;

    /// <summary>"Account" (all repos of a user/org) or "Repository" (one repo).</summary>
    public string Scope { get; set; } = "Account";

    /// <summary>
    /// Owner login this token uploads for, when Scope is "Account". Display only — a login is
    /// renameable and a repository can be transferred out from under it, so authorizing on this
    /// string means a token keeps working for an account that no longer owns the repository, and
    /// stops working for the one that does. <see cref="AccountGitHubId"/> is the authorization key.
    /// </summary>
    public string? AccountLogin { get; set; }

    /// <summary>
    /// GitHub's numeric id for the owner this token uploads for, when Scope is "Account". Null on
    /// tokens issued before this field existed, which fall back to comparing
    /// <see cref="AccountLogin"/> so that no working token is invalidated by a deploy.
    /// </summary>
    public long? AccountGitHubId { get; set; }

    /// <summary>GitHub repository id this token uploads for, when Scope is "Repository".</summary>
    public long? RepositoryGitHubId { get; set; }

    /// <summary>Free-text label telling you where this token is used, e.g. the CI workflow it was created for.</summary>
    /// <remarks>
    /// The breadcrumb, because it is the only field that means anything to a person. Marked
    /// explicitly rather than left to discovery: synchronization otherwise picked <c>Hash</c>, which
    /// would have put the token hash in every breadcrumb on the wire.
    /// </remarks>
    [Breadcrumb]
    public string? Description { get; set; }

    /// <summary>Id of the signed-in user who created this token.</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>When the token was created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the token was revoked (UTC); null while it is still valid for uploads.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>A fresh document id. The id is deliberately unrelated to the token.</summary>
    public static string NewDocumentId() => $"ApiTokens/{Guid.NewGuid():N}";
}
