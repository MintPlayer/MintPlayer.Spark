using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Single owner of client-secret hashing and verification.
/// <para>
/// Hashing previously lived only in consumer seed code, so every consumer had to
/// reproduce the algorithm exactly — and a mismatch fails silently as
/// <c>invalid_client</c> rather than loudly. Issuance and verification belong
/// together, here, for the same reason queue-name derivation and validation do.
/// </para>
/// <para>
/// Format is self-describing — <c>pbkdf2$sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>
/// — so the work factor can be raised later without invalidating stored secrets:
/// <see cref="Verify"/> reads the parameters from the stored value rather than
/// assuming today's.
/// </para>
/// </summary>
public static class ClientSecretHasher
{
    private const string Prefix = "pbkdf2$sha256$";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// Iteration count for newly hashed secrets. Client secrets are high-entropy
    /// machine credentials, so the work factor is not what makes them unguessable —
    /// the salt and the constant-time compare are what matter. This is deliberately
    /// moderate rather than password-grade (OWASP's 600k): verification runs on every
    /// token request, and a token endpoint that costs half a second per call is its
    /// own denial-of-service surface. It is high enough to protect an operator who
    /// sets a weak secret by hand.
    /// </summary>
    private const int Iterations = 100_000;

    /// <summary>Hashes a client secret for storage. Never store the secret itself.</summary>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a presented secret against a stored hash in constant time.
    /// Returns <see langword="false"/> for malformed stored values rather than throwing —
    /// a corrupt record must not authenticate, and must not crash the token endpoint either.
    /// </summary>
    public static bool Verify(string secret, string storedHash)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(storedHash))
            return false;
        if (!storedHash.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = storedHash[Prefix.Length..].Split('$');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Generates a new client secret: 256 bits of cryptographic randomness, urlsafe.
    /// Return this to the operator once — only its <see cref="Hash"/> is persisted.
    /// </summary>
    public static string GenerateSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
