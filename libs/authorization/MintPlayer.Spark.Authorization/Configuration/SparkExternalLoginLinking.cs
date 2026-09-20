namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// What happens when someone signs in with an external provider whose email already belongs to an
/// existing account.
/// </summary>
/// <remarks>
/// <para>
/// The situation is unavoidable once an application accepts more than one provider: the same person
/// signs in with GitHub today and GitLab tomorrow, and both assert the same address. Identity will
/// not link them on its own, and the default behaviour — refusing the second sign-in with a
/// duplicate-email error — is the one outcome that is always wrong, because it reads as "this
/// application is broken" rather than "you already have an account".
/// </para>
/// <para>
/// ⚠️ <b>Both linking modes are a way to take over an account if implemented carelessly.</b> An
/// unverified email from a provider is a claim, not a proof; linking on it alone would let anyone
/// who can create an account asserting your address inherit yours. That is why the modes differ in
/// <em>who proves what</em> rather than merely in convenience.
/// </para>
/// </remarks>
public enum SparkExternalLoginLinking
{
    /// <summary>
    /// Never link automatically. A second provider asserting a known address is refused, with an
    /// error that says so rather than a generic failure.
    /// </summary>
    /// <remarks>
    /// The safe default, and the right choice for an application with one provider — where the
    /// situation cannot arise, and any code path that could link is surface with no purpose.
    /// </remarks>
    Disabled,

    /// <summary>
    /// The account owner links providers themselves, while signed in. A second provider asserting a
    /// known address is refused at sign-in; linking happens from an account page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Proof comes from already holding the session: only someone who can sign in as the account
    /// can attach anything to it. That makes it the mode with the least to get wrong, and the
    /// reason it is worth supporting even though it asks more of the user.
    /// </para>
    /// <para>
    /// ⚠️ Requires a <b>last-credential guard</b>. A passwordless account whose only login is
    /// unlinked is permanently unreachable — no password to fall back to, and no provider left to
    /// prove ownership with.
    /// </para>
    /// </remarks>
    WhenSignedIn,

    /// <summary>
    /// Sign-in with a second provider does not sign in: it mails a confirmation to the address
    /// <b>already stored on the existing account</b>, and the link is made only when that link is
    /// followed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Proof comes from controlling the account's existing mailbox, which is what makes this usable
    /// by someone who cannot currently sign in at all — the case <see cref="WhenSignedIn"/> cannot
    /// serve.
    /// </para>
    /// <para>
    /// ⚠️ Two properties are non-negotiable, and dropping either turns this into an account
    /// takeover:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The mail goes to the address <b>already on the account</b>, never to the one the new provider
    /// just asserted. Mailing the asserted address would confirm nothing — the attacker chose it.
    /// </description></item>
    /// <item><description>
    /// The provider key captured when the mail was sent is <b>re-checked when the link is
    /// confirmed</b>. Without that, the confirmation token is a bearer credential that links
    /// <em>whatever identity presents it</em>, not the one it was issued for.
    /// </description></item>
    /// </list>
    /// <para>
    /// ⚠️ Requires a real mail transport. Configuring this with the framework's no-op sender means
    /// every confirmation is silently discarded and no link is ever made — a failure that looks
    /// exactly like nothing happening. Spark rejects that combination at startup.
    /// </para>
    /// </remarks>
    ConfirmByEmail,
}
