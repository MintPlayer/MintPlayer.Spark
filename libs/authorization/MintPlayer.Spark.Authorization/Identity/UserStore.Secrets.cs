using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

using System.Security.Cryptography;

namespace MintPlayer.Spark.Authorization.Identity;

// Secrets at rest (#460, D5): the authenticator key and external-login tokens are stored as
// "sdp1:" + IDataProtector.Protect(value). (Plain comment: the type's XML docs live in UserStore.cs —
// see UserStore.Passkeys.cs for why a second <summary> breaks the build.)
//
// - Legacy plaintext is still read: a value without the prefix is returned as-is, and
//   SparkUserBackfill rewrites it protected. Every write goes out protected.
// - Purpose strings carry no user id (ProtectorPurpose + the field kind). Raven assigns the id when
//   the document is first stored, so a value set before that could not be bound to it, and binding
//   adds nothing against the attacker it would address — one who can write user documents can
//   replace the secret outright. The field kind IS in the purpose, so a token's ciphertext cannot be
//   copied into the authenticator-key field.
// - An unreadable value never disables two-factor sign-in. A key ring that lost its keys (an
//   unmounted KeysPath after a redeploy) makes every protected value unreadable. Answering null for
//   the authenticator key would make Identity's IsTwoFactorEnabledAsync find no valid provider and
//   SKIP the second factor; so the store answers a fresh random key nobody holds instead, and logs an
//   error. Two-factor stays required, authenticator codes fail, recovery codes still work, and
//   resetting the authenticator key repairs the account. An unreadable external-login token reads as
//   absent (logged as a warning): the provider issues a new one at the next sign-in.
public partial class UserStore<TUser>
{
    /// <summary>The prefix marking a protected value.</summary>
    public const string ProtectedValuePrefix = "sdp1:";

    /// <summary>The root purpose of every protector this store creates.</summary>
    public const string ProtectorPurpose = "MintPlayer.Spark.Authorization.UserStore";

    internal const string AuthenticatorKeyPurpose = "AuthenticatorKey.v1";
    internal const string TokenPurpose = "Token.v1";


    private IDataProtector? authenticatorKeyProtector;
    private IDataProtector? tokenProtector;

    private IDataProtector ProtectorFor(string purpose)
    {
        if (dataProtectionProvider is null)
        {
            throw new InvalidOperationException(
                $"{nameof(UserStore<TUser>)} needs an {nameof(IDataProtectionProvider)} to store the authenticator key and "
                + "external-login tokens, and none was supplied. Resolve the store from DI (Spark registers Data Protection), "
                + "or pass one to the constructor.");
        }

        return purpose == AuthenticatorKeyPurpose
            ? authenticatorKeyProtector ??= dataProtectionProvider.CreateProtector(ProtectorPurpose, AuthenticatorKeyPurpose)
            : tokenProtector ??= dataProtectionProvider.CreateProtector(ProtectorPurpose, TokenPurpose);
    }

    /// <summary>Whether a stored value is already protected.</summary>
    internal static bool IsProtected(string? value)
        => value is not null && value.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal);

    internal string? Protect(string? value, string purpose)
    {
        if (value is null || IsProtected(value))
            return value;

        return ProtectedValuePrefix + ProtectorFor(purpose).Protect(value);
    }

    /// <summary>
    /// The plaintext of a stored value. Legacy plaintext passes through; a protected value that cannot
    /// be unprotected throws <see cref="CryptographicException"/> for the caller to decide.
    /// </summary>
    internal string? Unprotect(string? value, string purpose)
    {
        if (value is null || !IsProtected(value))
            return value;

        return ProtectorFor(purpose).Unprotect(value[ProtectedValuePrefix.Length..]);
    }

    private string? ReadAuthenticatorKey(TUser user)
    {
        try
        {
            return Unprotect(user.AuthenticatorKey, AuthenticatorKeyPurpose);
        }
        catch (CryptographicException ex)
        {
            logger?.LogError(ex,
                "The authenticator key of user {UserId} could not be unprotected — the Data Protection key ring no longer "
                + "holds the key it was protected with (Spark:DataProtection). Two-factor sign-in stays required: "
                + "authenticator codes fail, recovery codes still work, and resetting the authenticator key repairs the account.",
                user.Id);

            // A key nobody holds, new on every read: 2FA remains enabled (a null key would make
            // Identity find no valid provider and skip the second factor) and no code verifies.
            return UnknowableAuthenticatorKey();
        }
    }

    private string? ReadToken(TUser user, SparkUserToken token)
    {
        try
        {
            return Unprotect(token.Value, TokenPurpose);
        }
        catch (CryptographicException ex)
        {
            logger?.LogWarning(ex,
                "External-login token {Provider}/{Name} of user {UserId} could not be unprotected (Data Protection key ring "
                + "changed); it reads as absent until the provider issues a new one.",
                token.LoginProvider, token.Name, user.Id);
            return null;
        }
    }

    /// <summary>20 random bytes in RFC 4648 base32 — the shape Identity's authenticator provider decodes.</summary>
    private static string UnknowableAuthenticatorKey()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = RandomNumberGenerator.GetBytes(20);
        var chars = new char[32];
        int buffer = 0, bits = 0, index = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[index++] = alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }
        return new string(chars, 0, index);
    }

    /// <summary>
    /// Rewrites the user's legacy plaintext secrets protected, in memory. Returns whether anything
    /// changed. Used by <see cref="SparkUserBackfill{TUser}"/>; idempotent.
    /// </summary>
    internal bool ProtectLegacySecrets(TUser user)
    {
        var changed = false;

        if (user.AuthenticatorKey is not null && !IsProtected(user.AuthenticatorKey))
        {
            user.AuthenticatorKey = Protect(user.AuthenticatorKey, AuthenticatorKeyPurpose);
            changed = true;
        }

        foreach (var token in user.Tokens)
        {
            if (token.Value is not null && !IsProtected(token.Value))
            {
                token.Value = Protect(token.Value, TokenPurpose);
                changed = true;
            }
        }

        return changed;
    }
}
