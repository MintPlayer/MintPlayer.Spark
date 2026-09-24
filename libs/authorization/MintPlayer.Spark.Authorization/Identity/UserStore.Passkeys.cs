using Microsoft.AspNetCore.Identity;
using Raven.Client.Documents.Operations.CompareExchange;
using System.Security.Cryptography;

namespace MintPlayer.Spark.Authorization.Identity;

// IUserPasskeyStore<TUser> over the embedded SparkUser.Passkeys collection, plus the
// compare/exchange reservation that makes a credential id resolvable to its owner and unique across
// the cluster.
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

        // Reserve before mutating the document. If the reservation fails the credential belongs to
        // somebody else and enrollment must not proceed.
        if (!await CreatePasskeyReservationAsync(passkey.CredentialId, user.Id, cancellationToken))
        {
            // Deliberately says nothing about who holds it. Naming the other account would turn
            // enrollment into a lookup oracle over a value the caller already controls.
            throw new InvalidOperationException("This passkey is already registered.");
        }

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

        var operations = documentStore.Operations.ForDatabase(DatabaseName);
        var result = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(PasskeyKeyFor(credentialId)), token: cancellationToken);

        if (string.IsNullOrEmpty(result?.Value))
            return null;

        return await Session.LoadAsync<TUser>(result.Value, cancellationToken);
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
    public async Task RemovePasskeyAsync(TUser user, byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credentialId);

        var existing = FindEmbeddedPasskey(user, credentialId);
        if (existing is null)
            return;

        user.Passkeys.Remove(existing);
        await DeletePasskeyReservationAsync(credentialId, cancellationToken);
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

    /// <summary>
    /// Takes the cluster-wide reservation for a credential id.
    /// <para>
    /// Idempotent for the same owner: a reservation that already points at this user is treated as
    /// success, so a retry after the document write failed can complete rather than being locked out
    /// by its own half-finished attempt.
    /// </para>
    /// </summary>
    private async Task<bool> CreatePasskeyReservationAsync(byte[] credentialId, string userId, CancellationToken cancellationToken)
    {
        var key = PasskeyKeyFor(credentialId);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);

        var result = await operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(key, userId, 0), token: cancellationToken);

        if (result.Successful)
            return true;

        var existing = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);

        return string.Equals(existing?.Value, userId, StringComparison.Ordinal);
    }

    private async Task DeletePasskeyReservationAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        var key = PasskeyKeyFor(credentialId);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);

        var existing = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);

        if (existing != null)
        {
            await operations.SendAsync(
                new DeleteCompareExchangeValueOperation<string>(key, existing.Index), token: cancellationToken);
        }
    }

    /// <summary>
    /// Releases every passkey reservation a user holds. Called from <c>DeleteAsync</c>: without it a
    /// deleted user's credential ids stay reserved forever, pointing at a document that no longer
    /// exists.
    /// </summary>
    private async Task DeleteAllPasskeyReservationsAsync(TUser user, CancellationToken cancellationToken)
    {
        foreach (var passkey in user.Passkeys)
        {
            await DeletePasskeyReservationAsync(passkey.CredentialId, cancellationToken);
        }
    }

    #endregion
}
