using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// A password hash that belongs to no account, made once per application by the configured
/// <see cref="IPasswordHasher{TUser}"/>. <see cref="SparkSignInManager{TUser}"/> verifies the supplied
/// password against it whenever there is no real hash to check — an unknown identifier, or an account
/// without a password — so those answers cost the same hashing as a wrong password (O27).
/// </summary>
/// <remarks>
/// A singleton rather than a constant: the hash must come from the same hasher with the same options
/// (algorithm, PBKDF2 iteration count) as real hashes, or the verification would take a different
/// time. The hasher itself is scoped, so the first caller's instance produces it; every instance in an
/// application shares one configuration.
/// </remarks>
public sealed class SparkUnknownUserPasswordHash<TUser>
    where TUser : SparkUser, new()
{
    private string? hash;

    /// <summary>
    /// Verifies <paramref name="password"/> against the dummy hash and discards the result. Never
    /// succeeds in any way that matters: the caller answers <see cref="SignInResult.Failed"/> regardless.
    /// </summary>
    public void Verify(IPasswordHasher<TUser> hasher, string? password)
    {
        var user = new TUser();
        // Benign race: two first callers may each hash once; either result is equally valid.
        hash ??= hasher.HashPassword(user, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        hasher.VerifyHashedPassword(user, hash, password ?? string.Empty);
    }
}
