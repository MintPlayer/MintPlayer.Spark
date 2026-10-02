using CodeCoverage.LookupReferences;
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
    [LookupReference(typeof(ApiTokenScope))]
    public string Scope { get; set; } = ApiTokenScope.Account;

    /// <summary>
    /// The account this token uploads for: the <see cref="Entities.Account"/> document id,
    /// <c>Accounts/{provider}/{id}</c>.
    /// </summary>
    /// <remarks>
    /// <b>The one field that decides the owner.</b> The forge and the numeric id are both in the
    /// document id, so a token cannot name an account on one forge and a provider of another, and a
    /// login rename or a repository transfer changes nothing here. The owner's login and
    /// <c>provider:login</c> key are not stored on the token: <c>VApiToken</c> reads them from
    /// this account through <c>ApiTokens_Overview</c>.
    /// <para>
    /// Read-only in the model and never posted: on create <c>ApiTokenActions.OnBeforeSaveAsync</c>
    /// takes it from the account the New was started from (<c>obj.Parent</c>, resolved and authorized
    /// by Spark), and an edit keeps it. It is re-authorized on every save.
    /// </para>
    /// <para>
    /// Replaced <c>AccountLogin</c>, <c>AccountOwnerKey</c>, <c>Provider</c> and <c>AccountId</c>
    /// on 2026-10-02 (<c>M_202610021200_ApiTokenReferencesItsAccount</c>).
    /// </para>
    /// </remarks>
    [Reference(typeof(Account))]
    public string? Account { get; set; }

    /// <summary>
    /// Document ids of the repositories this token may upload for. Empty means the token is
    /// account-scoped and covers every repository of <see cref="Account"/>.
    /// </summary>
    /// <remarks>
    /// Replaces a single numeric <c>RepositoryGitHubId</c>: a token often serves several
    /// repositories, and a person should never be asked to type a GitHub id. The picker lists the
    /// parent account's repositories (<c>Custom.Account_Repositories</c>, the Account page's card) —
    /// the same account as <see cref="Account"/>, which <c>OnBeforeSaveAsync</c> enforces.
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
    [Reference(typeof(Repository), "Account_Repositories")]
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
