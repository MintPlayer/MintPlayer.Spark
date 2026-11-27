using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.Storage;
using System.Security.Cryptography;

namespace MintPlayer.Spark.Authorization.Identity;

// IUserPasskeyStore<TUser> over the embedded SparkUser.Passkeys collection, plus the
// store reservation (#536 S6; compare exchange on RavenDB, under the same keys) that makes a credential id resolvable
// to its owner and unique.
//
// ⚠️ Plain comment, not an XML <summary>: the type's docs live on the primary declaration in
// UserStore.cs. A partial type documented in two files makes DescriptionSourceGenerator emit two
// [Description] attributes for the same symbol, which fails the build with CS0579.
public partial class UserStore<TUser>
{
    private const string PasskeyReservationKeyPrefix = "passkeys/";

    /// <summary>
    /// A credential id may be up to 1023 bytes, whose base64url form will not fit a compare/exchange
    /// key, so the key is built over a SHA-256 of it instead — a fixed 43 characters.
    /// <para>
    /// The hash is used here as a content-addressed identifier, not as a security boundary: a
    /// credential id is public data, and collision resistance is the only property being relied on.
    /// </para>
    /// </summary>
    private static string PasskeyKeyFor(byte[] credentialId)
        => PasskeyReservationKeyPrefix + Base64UrlEncode(SHA256.HashData(credentialId));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    #region IUserPasskeyStore

    /// <summary>
    /// Adds a new passkey, or refreshes the mutable half of one already enrolled.
    /// <para>
    /// The write-once fields — credential id, public key, creation time, attestation — are never
    /// overwritten on an update. Only <see cref="UserPasskeyInfo.SignCount"/>,
    /// <see cref="UserPasskeyInfo.IsUserVerified"/>, <see cref="UserPasskeyInfo.IsBackedUp"/> and
    /// the user's label change over a credential's life, and the sign count is why this method is
    /// called after every successful assertion rather than only at enrollment.
    /// </para>
    /// </summary>
    public async Task AddOrUpdatePasskeyAsync(TUser user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(passkey);

        var existing = FindEmbeddedPasskey(user, passkey.CredentialId);
        if (existing is not null)
        {
            existing.SignCount = passkey.SignCount;
            existing.IsUserVerified = passkey.IsUserVerified;
            existing.IsBackedUp = passkey.IsBackedUp;
            existing.Name = passkey.Name;
            return;
        }

        if (string.IsNullOrEmpty(user.Id))
            throw new InvalidOperationException("The user must be persisted before a passkey can be enrolled against it.");

        // Checked before mutating the document: a credential that belongs to somebody else must not be enrolled. The
        // claim itself is written with the user (UpdateAsync's save); a concurrent enrollment of the same id fails that
        // save instead.
        var key = PasskeyKeyFor(passkey.CredentialId);
        var holder = await Session.GetReservationAsync(key, cancellationToken);
        if (holder is not null && !string.Equals(holder, user.Id, StringComparison.OrdinalIgnoreCase)
            && await Session.ExistsAsync(user.GetType(), holder, cancellationToken))
        {
            // Deliberately says nothing about who holds it. Naming the other account would turn
            // enrollment into a lookup oracle over a value the caller already controls.
            throw new InvalidOperationException("This passkey is already registered.");
        }
        Session.Reserve(key, user.Id);

        user.Passkeys.Add(new SparkUserPasskey
        {
            CredentialId = passkey.CredentialId,
            PublicKey = passkey.PublicKey,
            CreatedAt = passkey.CreatedAt,
            Transports = passkey.Transports ?? [],
            AttestationObject = passkey.AttestationObject,
            ClientDataJson = passkey.ClientDataJson,
            IsBackupEligible = passkey.IsBackupEligible,
            Name = passkey.Name,
            SignCount = passkey.SignCount,
            IsUserVerified = passkey.IsUserVerified,
            IsBackedUp = passkey.IsBackedUp,
        });
    }

    /// <summary>
    /// Resolves a credential id to its owner.
    /// <para>
    /// ⚠️ Called on the <b>enrollment</b> path, not on sign-in. <c>PasskeyHandler</c> uses it to
    /// refuse a credential id that is already registered to anybody; an assertion resolves the user
    /// from the user handle via <c>FindByIdAsync</c> and never queries by credential id.
    /// </para>
    /// <para>
    /// That is why it reads the compare/exchange value and then loads by id — strongly consistent —
    /// rather than querying an index. A stale index here would not slow a sign-in down, it would let
    /// the same credential be registered to two accounts, silently.
    /// </para>
    /// </summary>
    public async Task<TUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(credentialId);

        var userId = await Session.GetReservationAsync(PasskeyKeyFor(credentialId), cancellationToken);
        return string.IsNullOrEmpty(userId) ? null : Loaded(await Session.LoadAsync<TUser>(userId, cancellationToken));
    }

    public Task<UserPasskeyInfo?> FindPasskeyAsync(TUser user, byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credentialId);

        var found = FindEmbeddedPasskey(user, credentialId);
        return Task.FromResult(found is null ? null : ToPasskeyInfo(found));
    }

    public Task<IList<UserPasskeyInfo>> GetPasskeysAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);

        IList<UserPasskeyInfo> passkeys = [.. user.Passkeys.Select(ToPasskeyInfo)];
        return Task.FromResult(passkeys);
    }

    /// <summary>
    /// Removes a passkey and releases its reservation, so the credential id becomes registerable
    /// again. Leaving the reservation behind would burn the id permanently.
    /// </summary>
    public Task RemovePasskeyAsync(TUser user, byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credentialId);

        var existing = FindEmbeddedPasskey(user, credentialId);
        if (existing is null)
            return Task.CompletedTask;

        user.Passkeys.Remove(existing);
        // Freed with the user's save, and only while this user holds it.
        if (user.Id is not null)
            Session.Release(PasskeyKeyFor(credentialId), user.Id);
        return Task.CompletedTask;
    }

    #endregion

    #region Passkey helpers

    private static SparkUserPasskey? FindEmbeddedPasskey(TUser user, byte[] credentialId)
        => user.Passkeys.FirstOrDefault(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));

    private static UserPasskeyInfo ToPasskeyInfo(SparkUserPasskey passkey)
        => new(
            passkey.CredentialId,
            passkey.PublicKey,
            passkey.CreatedAt,
            passkey.SignCount,
            passkey.Transports,
            passkey.IsUserVerified,
            passkey.IsBackupEligible,
            passkey.IsBackedUp,
            passkey.AttestationObject,
            passkey.ClientDataJson)
        {
            Name = passkey.Name,
        };

    #endregion
}
