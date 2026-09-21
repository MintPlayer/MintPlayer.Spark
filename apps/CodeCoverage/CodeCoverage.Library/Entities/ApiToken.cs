using CodeCoverage.Forge;
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
    /// stops working for the one that does. <see cref="AccountId"/> is the authorization key.
    /// </summary>
    public string? AccountLogin { get; set; }

    /// <summary>
    /// The owning account as <c>provider:login</c> — what the row filter compares.
    /// </summary>
    /// <remarks>
    /// ⚠️ See <see cref="Repository.OwnerKey"/>. A token scoped to a bare login would authorise
    /// uploads for the same-named owner on <em>any</em> forge, and an upload token is exactly the
    /// credential where that must not be possible.
    /// </remarks>
    public string? AccountOwnerKey { get; set; }

    /// <summary>The forge this token uploads to.</summary>
    /// <remarks>
    /// ⚠️ <b>Without this, <see cref="AccountId"/> is ambiguous.</b> A numeric account id is unique
    /// only <em>within</em> a forge — GitHub user 1234 and GitLab group 1234 are different
    /// principals — so a token carrying an id and no forge would authorize uploads for whichever
    /// one the reader happened to assume. The upload path assumed GitHub, as a literal, until
    /// 2026-09-22.
    /// <para>
    /// Defaults to GitHub, which is correct for every token that existed before the field: it was
    /// the only forge.
    /// </para>
    /// </remarks>
    [IgnoreProperty]
    public EForgeProvider Provider { get; set; } = EForgeProvider.GitHub;

    /// <summary>
    /// The forge's numeric id for the owner this token uploads for, when Scope is "Account".
    /// </summary>
    /// <remarks>
    /// <b>The authorization key</b>, and the reason it is the numeric id rather than
    /// <see cref="AccountOwnerKey"/>: a key is <c>provider:login</c> and therefore login-derived, so
    /// it is wrong in both directions once a repository is transferred — the old owner's token keeps
    /// working for a repository they no longer own, and the new owner's does not work for one they
    /// do. A numeric id survives a rename, which is exactly what makes it safe to authorize on.
    /// <para>
    /// Null on tokens issued before the field existed, which fall back to comparing
    /// <see cref="AccountLogin"/> so no working token is invalidated by a deploy.
    /// <c>M_202609221000</c> backfills what it can and reports what it cannot; the fallback can go
    /// once that list is empty.
    /// </para>
    /// <para>
    /// ⚠️ <c>[IgnoreProperty]</c> is Spark's, not RavenDB's — the property stays on the document and
    /// keeps being written and read; it is only absent from the <em>model</em>. It is stamped
    /// server-side from the resolved account, so nobody should ever type it into a form.
    /// </para>
    /// <para>
    /// Was <c>AccountGitHubId</c> until 2026-09-22. Renamed with <c>M_202609220950</c>, because a
    /// name is a contract when it is the name of a stored field.
    /// </para>
    /// </remarks>
    [IgnoreProperty]
    public long? AccountId { get; set; }

    /// <summary>
    /// Document ids of the repositories this token may upload for. Empty means the token is
    /// account-scoped and covers every repository of <see cref="AccountId"/>.
    /// </summary>
    /// <remarks>
    /// Replaces a single numeric <c>RepositoryGitHubId</c>: a token often serves several
    /// repositories, and a person should never be asked to type a GitHub id. The picker lists the
    /// repositories the caller manages.
    /// <para>
    /// ⚠️ <b>Document ids, not GitHub ids.</b> Every layer of the reference machinery — the
    /// synchronizer, <c>EntityMapper</c>, <c>BreadcrumbResolver</c>, <c>ReferenceResolver</c>'s
    /// <c>.Include()</c>, and the Angular picker, which can only emit <c>po.id</c> — treats an
    /// element as a document id. A <c>List&lt;long&gt;</c> of GitHub ids would synchronize happily
    /// and then resolve to nothing.
    /// </para>
    /// <para>
    /// ⚠️ <b>This field decides who may upload where, and the array write path validates nothing</b>
    /// — it bypasses the collection guard that protects a scalar reference. Every id is re-checked
    /// against the caller's own repositories in <c>ApiTokenActions.OnBeforeSaveAsync</c>, on edit as
    /// well as create. Without that check a signed-in user could scope a token to any repository by
    /// posting its id.
    /// </para>
    /// </remarks>
    [Reference(typeof(Repository), "ApiToken_SelectableRepositories")]
    public List<string> RepositoryIds { get; set; } = [];

    /// <summary>Free-text label telling you where this token is used, e.g. the CI workflow it was created for.</summary>
    /// <remarks>
    /// The breadcrumb, because it is the only field that means anything to a person. Marked
    /// explicitly rather than left to discovery: synchronization otherwise picked <c>Hash</c>, which
    /// would have put the token hash in every breadcrumb on the wire.
    /// </remarks>
    [Breadcrumb]
    public string? Description { get; set; }

    /// <summary>The signed-in user who created this token.</summary>
    /// <remarks>
    /// A reference so the grid shows a username rather than a document id. Stamped server-side in
    /// <c>OnBeforeSaveAsync</c> and read-only in the model: it was previously writable and stamped
    /// by nothing, so every token created through the UI carried an empty string and a client could
    /// have claimed to be anyone.
    /// <para>
    /// <c>SparkUser</c> is deliberately NOT a context root. Its model file is hand-authored and
    /// declares only <c>Id</c> and <c>UserName</c> — registering the type would have synchronize
    /// generate <c>PasswordHash</c>, <c>SecurityStamp</c> and the rest as model attributes.
    /// </para>
    /// </remarks>
    [Reference(typeof(MintPlayer.Spark.Authorization.Identity.SparkUser))]
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>When the token was created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the token was revoked (UTC); null while it is still valid for uploads.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>A fresh document id. The id is deliberately unrelated to the token.</summary>
    public static string NewDocumentId() => $"ApiTokens/{Guid.NewGuid():N}";
}
