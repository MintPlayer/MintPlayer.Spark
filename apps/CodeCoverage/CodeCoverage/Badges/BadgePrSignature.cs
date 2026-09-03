using System.Security.Cryptography;
using System.Text;

namespace CodeCoverage.Badges;

/// <summary>
/// A capability for ONE pull request's badge, so the bot can put a working
/// badge image in a private repository's PR comment without publishing
/// <c>Repository.BadgeToken</c>.
/// <para>
/// Why not just use the badge token: it is manager-only today (redacted in
/// RepositoryActions and BrowseController), it is repo-wide, it never expires,
/// and rotating it breaks every README badge at once. A comment is read by
/// every collaborator and gets quoted onward, so the credential in it should be
/// worth as little as possible. This one is worth exactly one PR's coverage
/// number.
/// </para>
/// <para>
/// Deterministic on purpose: re-rendering the sticky comment must produce the
/// same URL, or GitHub's camo proxy re-fetches on every edit and the reader
/// sees a flash of unloaded image.
/// </para>
/// </summary>
public static class BadgePrSignature
{
    public const string KeyConfigurationPath = "Coverage:BadgeSigningKey";

    private const string Purpose = "badge-pr";

    /// <summary>
    /// Hex signature for (<paramref name="gitHubId"/>, <paramref name="pullRequestNumber"/>), or null
    /// when no signing key is configured.
    /// <para>
    /// A null return must degrade to "no image" rather than "unsigned image":
    /// callers treat it as an absent capability, and <see cref="Verify"/> fails
    /// closed for the same reason.
    /// </para>
    /// </summary>
    public static string? Compute(IConfiguration configuration, long gitHubId, int pullRequestNumber)
    {
        var key = configuration[KeyConfigurationPath];
        if (string.IsNullOrEmpty(key)) return null;

        // The purpose string keeps this signature from ever validating in some
        // other context that happens to sign the same two numbers.
        var payload = $"{Purpose}\n{gitHubId}\n{pullRequestNumber}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));

        // 128 bits is far beyond guessable for a value that only ever reveals
        // one already-readable coverage percentage, and keeps the URL short
        // enough to read in a comment's source.
        return Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }

    /// <summary>
    /// Constant-time check of <paramref name="presented"/> against the signature for this
    /// (repository, pull request). False when no key is configured.
    /// </summary>
    public static bool Verify(IConfiguration configuration, long gitHubId, int pullRequestNumber, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var expected = Compute(configuration, gitHubId, pullRequestNumber);
        if (string.IsNullOrEmpty(expected)) return false;

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
