namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// An external login waiting to be attached to an existing account, once the account's owner says so.
/// </summary>
/// <remarks>
/// <para>
/// Written when someone signs in with a provider whose email already belongs to an account, under
/// <see cref="Configuration.SparkExternalLoginLinking.ConfirmByEmail"/>. The sign-in does
/// <b>not</b> succeed at that point — a document is stored, a mail goes out, and the link is made
/// only when it is confirmed.
/// </para>
/// <para>
/// ⚠️ <b>This is not an email confirmation, and deliberately not built on one.</b> Confirming an
/// email attests that an address belongs to you; this authorises attaching a <em>credential</em> to
/// an account. Reusing an account-confirmation token here would conflate the two, so that anyone
/// holding a token issued for an unrelated purpose — possibly much earlier — could attach a login.
/// The two decisions stay separate because they are separate.
/// </para>
/// <para>
/// The document is the state; the token is the capability. Storing one hash rather than the token
/// means a database read cannot yield a usable link capability.
/// </para>
/// </remarks>
public class SparkPendingExternalLogin
{
    /// <summary>Document id: <c>SparkPendingExternalLogins/{hash}</c>, hashed from the triple below.</summary>
    /// <remarks>
    /// Derived rather than random, which is a change of mind worth recording. A random id would have
    /// forced the "is one already pending?" check to be a <em>query</em>, and an auto-index that is
    /// stale at the wrong moment turns that check into "no" — which mails a second confirmation.
    /// Deriving the key makes it a load. It leaks nothing about the account because it is a hash,
    /// and it is not the capability, so carrying it in the mailed token costs nothing.
    /// </remarks>
    public string? Id { get; set; }

    /// <summary>SHA-256 of the single-use token that was mailed, hex-encoded.</summary>
    /// <remarks>
    /// Only the hash is stored, for the same reason an upload token stores only its hash: a leaked
    /// backup or an over-broad query must not yield something that can be presented.
    /// </remarks>
    public required string TokenHash { get; set; }

    /// <summary>The existing account the login would be attached to.</summary>
    public required string UserId { get; set; }

    /// <summary>The provider that asked, as ASP.NET Identity names it.</summary>
    public required string LoginProvider { get; set; }

    /// <summary>
    /// The provider's key for that identity, captured when the mail was sent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Re-checked on confirm, and that check is non-negotiable.</b> Without it the mailed
    /// token is a bearer credential that links <em>whichever</em> identity happens to present it,
    /// rather than the one it was issued for — so a second person signing in with the same provider
    /// between send and confirm would inherit the link.
    /// </remarks>
    public required string ProviderKey { get; set; }

    /// <summary>Display name of the provider identity, for the mail body ("as octocat").</summary>
    public string? ProviderDisplayName { get; set; }

    /// <summary>When the capability stops being honoured.</summary>
    /// <remarks>
    /// Enforced on read rather than trusted to expiry alone, because a document that outlives its
    /// window through a stalled reaper must not still be usable. RavenDB's <c>@refresh</c> removes
    /// it eventually; the check is what makes that removal a tidy-up rather than a security control.
    /// </remarks>
    public required DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>When it was consumed. Non-null means it is spent and must never link again.</summary>
    /// <remarks>
    /// Kept rather than deleted so a second click is answerable with "already used" instead of the
    /// same message as an expired or forged token — and so the attempt is visible at all.
    /// </remarks>
    public DateTimeOffset? ConsumedAtUtc { get; set; }

    /// <summary>Whether this pending link may still be honoured, as of <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => ConsumedAtUtc is null && now < ExpiresAtUtc;
}
