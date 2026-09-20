using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// What happened when an external login asked to be attached to an account that already exists.
/// </summary>
public enum SparkLinkRequestOutcome
{
    /// <summary>Linking is switched off. The sign-in is refused and nothing is written.</summary>
    Refused,

    /// <summary>
    /// The account owner must link this provider themselves, from a session they already hold.
    /// </summary>
    RequiresSignIn,

    /// <summary>
    /// A confirmation is on its way to the address already stored on the existing account.
    /// </summary>
    /// <remarks>
    /// ⚠️ Also returned when a usable confirmation for the same provider identity was already
    /// pending and <b>no second mail was sent</b>. The caller must not be able to tell the two
    /// apart, or repeated sign-in attempts become a way to flood someone else's mailbox.
    /// </remarks>
    ConfirmationSent,
}

/// <summary>Why a confirmation link did not attach anything.</summary>
public enum SparkLinkConfirmationOutcome
{
    /// <summary>The login is now attached to the account.</summary>
    Linked,

    /// <summary>No such pending link, or the token does not match, or its window has passed.</summary>
    /// <remarks>
    /// The three are deliberately one outcome. Distinguishing them tells someone holding a guessed
    /// token whether the guess was close, and tells anyone whether a given account has a link
    /// pending.
    /// </remarks>
    InvalidOrExpired,

    /// <summary>The link was already followed once.</summary>
    /// <remarks>
    /// Kept separate from <see cref="InvalidOrExpired"/> only because it is answerable: a second
    /// click by the same person is a normal thing to do, and telling them it is already done is
    /// better than telling them their link is broken.
    /// </remarks>
    AlreadyUsed,

    /// <summary>
    /// The request carried an external identity that is not the one the confirmation was issued for.
    /// </summary>
    ProviderMismatch,

    /// <summary>The account disappeared between the request and the confirmation.</summary>
    AccountGone,
}

/// <summary>The result of following a confirmation link, with the account it belongs to.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="UserId">
/// The account that was linked, when <paramref name="Outcome"/> is
/// <see cref="SparkLinkConfirmationOutcome.Linked"/>. <see langword="null"/> otherwise — a failed
/// confirmation must not hand the caller an account id it did not already have.
/// </param>
public readonly record struct SparkLinkConfirmationResult(
    SparkLinkConfirmationOutcome Outcome,
    string? UserId);

/// <summary>
/// Owns the <see cref="Configuration.SparkExternalLoginLinking.ConfirmByEmail"/> half of external
/// login linking: writing the pending document, mailing the request, and honouring the confirmation.
/// </summary>
/// <remarks>
/// <para>
/// A service rather than code in the callback endpoint, because the two halves are separated by a
/// mail round-trip and an unbounded amount of time. Keeping them in one type is the only way the
/// token format, the document key and the provider-key re-check stay describable as a single
/// mechanism — split across two endpoint lambdas they drift, and the drift is a security bug rather
/// than an inconsistency.
/// </para>
/// <para>
/// ⚠️ <b>The confirmation attaches the provider key captured when the mail was sent</b>, never one
/// read from whatever session happens to be present when the link is followed. That is what makes
/// the token safe to mail: it is a capability to attach <em>one specific identity</em>, not a
/// capability to attach whoever is holding it. See <see cref="ConfirmAsync"/>.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The application's user type.</typeparam>
public partial class SparkExternalLoginLinker<TUser> where TUser : SparkUser, new()
{
    /// <summary>
    /// How long a mailed confirmation stays usable.
    /// </summary>
    /// <remarks>
    /// Deliberately short. The person who triggered this is at their keyboard right now, so a long
    /// window buys them nothing — it only widens the period in which a mail obtained later still
    /// attaches a credential. If the request was <em>not</em> theirs, an expired link costs them
    /// nothing: no link is made either way.
    /// </remarks>
    internal static readonly TimeSpan ConfirmationWindow = TimeSpan.FromHours(1);

    private const string IdPrefix = "SparkPendingExternalLogins/";

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ISparkLinkConfirmationSender<TUser>? confirmationSender;

    /// <summary>
    /// Records a pending link and asks the account's owner to confirm it.
    /// </summary>
    /// <param name="user">
    /// The <b>existing</b> account, found by the email the provider asserted. The mail goes to the
    /// address on this user — see <see cref="ISparkLinkConfirmationSender{TUser}"/> for why that is
    /// not negotiable.
    /// </param>
    /// <param name="loginProvider">The provider scheme, as Identity names it.</param>
    /// <param name="providerKey">The provider's key for the identity that just signed in.</param>
    /// <param name="providerDisplayName">How to name the provider to a human reader.</param>
    /// <param name="providerIdentity">How the provider named the person, for the mail body.</param>
    /// <param name="buildConfirmationLink">
    /// Turns the single-use token into the absolute URL to put in the mail. Supplied by the caller
    /// because only the request knows the origin, and a link built from a
    /// <c>Host</c> header an attacker controls would point the confirmation at their server.
    /// </param>
    public async Task<SparkLinkRequestOutcome> RequestLinkAsync(
        TUser user,
        string loginProvider,
        string providerKey,
        string providerDisplayName,
        string? providerIdentity,
        Func<string, string> buildConfirmationLink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Id is null)
            throw new InvalidOperationException("Cannot request a link for an unsaved user.");

        // Registered at startup when the mode is ConfirmByEmail — the guard in
        // LocalCredentialEndpointFilter refuses the configuration otherwise — so reaching here with
        // no sender means someone bypassed MapSparkAuthentication. Fail loudly rather than write a
        // pending document nobody will ever be told about.
        if (confirmationSender is null)
            throw new InvalidOperationException(
                $"No {nameof(ISparkLinkConfirmationSender<TUser>)} is registered, so no link "
                + "confirmation can be sent.");

        var documentId = IdPrefix + DeriveKey(user.Id, loginProvider, providerKey);
        var now = timeProvider.GetUtcNow();

        using var session = documentStore.OpenAsyncSession();
        var existing = await session.LoadAsync<SparkPendingExternalLogin>(documentId, cancellationToken);

        // A second attempt while one is still usable sends nothing. Without this, signing in
        // repeatedly with a provider whose address belongs to someone else is a mail flood aimed at
        // them, and every message reads as an account-takeover attempt.
        if (existing is not null && existing.IsUsable(now))
            return SparkLinkRequestOutcome.ConfirmationSent;

        var secret = GenerateSecret();

        // ⚠️ A spent or expired document is *rewritten in place*, not replaced. Storing a second
        // instance at the same id throws NonUniqueObjectException — the load above already
        // associated one with this session — and the case that reaches it is the ordinary one:
        // somebody confirmed a link, then asked to link another provider later.
        if (existing is not null)
        {
            existing.TokenHash = HashSecret(secret);
            existing.ProviderDisplayName = providerDisplayName;
            existing.ExpiresAtUtc = now + ConfirmationWindow;
            existing.ConsumedAtUtc = null;
        }
        else
        {
            await session.StoreAsync(new SparkPendingExternalLogin
            {
                Id = documentId,
                TokenHash = HashSecret(secret),
                UserId = user.Id,
                LoginProvider = loginProvider,
                ProviderKey = providerKey,
                ProviderDisplayName = providerDisplayName,
                ExpiresAtUtc = now + ConfirmationWindow,
            }, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);

        var link = buildConfirmationLink(FormatToken(documentId, secret));
        await confirmationSender.SendLinkConfirmationAsync(
            user, providerDisplayName, providerIdentity, link, cancellationToken);

        return SparkLinkRequestOutcome.ConfirmationSent;
    }

    /// <summary>
    /// Honours a confirmation link: attaches the captured login to the account and spends the token.
    /// </summary>
    /// <param name="token">The opaque value from the mailed link.</param>
    /// <param name="presentedProviderKey">
    /// The provider key of an external identity present on the request, if there is one.
    /// <see langword="null"/> when the link is followed straight from a mailbox, which is the normal
    /// case.
    /// </param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The link that gets attached is read from the stored document</b>, so a mismatch is not
    /// something this method has to catch to be safe — the ambient identity is never what gets
    /// linked in the first place. <paramref name="presentedProviderKey"/> is checked anyway,
    /// because a request that carries a <em>different</em> identity than the one the confirmation
    /// was issued for is a sign of exactly the substitution this design is built against, and
    /// refusing it costs a legitimate user nothing.
    /// </para>
    /// <para>
    /// The document is marked consumed <b>before</b> the login is attached, so a failure to attach
    /// cannot leave a token that is still spendable.
    /// </para>
    /// </remarks>
    public async Task<SparkLinkConfirmationResult> ConfirmAsync(
        string? token,
        string? presentedProviderKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseToken(token, out var documentId, out var secret))
            return new(SparkLinkConfirmationOutcome.InvalidOrExpired, null);

        using var session = documentStore.OpenAsyncSession();
        var pending = await session.LoadAsync<SparkPendingExternalLogin>(documentId, cancellationToken);
        if (pending is null)
            return new(SparkLinkConfirmationOutcome.InvalidOrExpired, null);

        // Fixed-time comparison: the hash is what the document reveals, and a timing-distinguishable
        // compare against it is the one oracle that would make guessing the secret feasible.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(pending.TokenHash),
                Encoding.ASCII.GetBytes(HashSecret(secret))))
            return new(SparkLinkConfirmationOutcome.InvalidOrExpired, null);

        if (pending.ConsumedAtUtc is not null)
            return new(SparkLinkConfirmationOutcome.AlreadyUsed, null);

        var now = timeProvider.GetUtcNow();
        if (now >= pending.ExpiresAtUtc)
            return new(SparkLinkConfirmationOutcome.InvalidOrExpired, null);

        if (presentedProviderKey is not null && !string.Equals(
                presentedProviderKey, pending.ProviderKey, StringComparison.Ordinal))
            return new(SparkLinkConfirmationOutcome.ProviderMismatch, null);

        var user = await userManager.FindByIdAsync(pending.UserId);
        if (user is null)
            return new(SparkLinkConfirmationOutcome.AccountGone, null);

        pending.ConsumedAtUtc = now;
        await session.SaveChangesAsync(cancellationToken);

        var result = await userManager.AddLoginAsync(user, new UserLoginInfo(
            pending.LoginProvider, pending.ProviderKey, pending.ProviderDisplayName ?? pending.LoginProvider));

        // LoginAlreadyAssociated means the link this token asked for already exists — the outcome
        // the user wanted, reached another way. Anything else is a real store failure, and the token
        // is already spent, so saying "invalid" is the honest answer rather than a retry that would
        // not work.
        if (!result.Succeeded)
            return result.Errors.Any(e => e.Code == "LoginAlreadyAssociated")
                ? new(SparkLinkConfirmationOutcome.Linked, user.Id)
                : new(SparkLinkConfirmationOutcome.InvalidOrExpired, null);

        return new(SparkLinkConfirmationOutcome.Linked, user.Id);
    }

    /// <summary>
    /// The document key for a pending link, derived from the triple it is about.
    /// </summary>
    /// <remarks>
    /// Derived rather than random so that a second request for the <em>same</em> link finds the
    /// first one with a load instead of a query — which is what makes the no-second-mail rule above
    /// possible without depending on an index that may be stale at exactly the wrong moment. It is
    /// a hash, so the key reveals nothing about the account it belongs to; and because it is not the
    /// capability, carrying it in the mailed token costs nothing.
    /// </remarks>
    private static string DeriveKey(string userId, string loginProvider, string providerKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\u001f{loginProvider}\u001f{providerKey}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    private static string GenerateSecret() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string HashSecret(string secret)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// The mailed token: the document key and the secret in one opaque value.
    /// </summary>
    /// <remarks>
    /// One parameter rather than two, so a link cannot be assembled half-correctly — and so the
    /// secret never appears on its own in a place that might treat it as an identifier worth
    /// logging.
    /// </remarks>
    private static string FormatToken(string documentId, string secret)
        => $"{documentId[IdPrefix.Length..]}.{secret}";

    private static bool TryParseToken(string? token, out string documentId, out string secret)
    {
        documentId = string.Empty;
        secret = string.Empty;
        if (string.IsNullOrEmpty(token))
            return false;

        var separator = token.IndexOf('.');
        if (separator <= 0 || separator == token.Length - 1)
            return false;

        var keyPart = token[..separator];
        // The key half is interpolated into a document id, so it is constrained to what DeriveKey
        // can produce. Without this a crafted token addresses any document in the database.
        if (keyPart.Length != 32 || !keyPart.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;

        documentId = IdPrefix + keyPart;
        secret = token[(separator + 1)..];
        return true;
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
