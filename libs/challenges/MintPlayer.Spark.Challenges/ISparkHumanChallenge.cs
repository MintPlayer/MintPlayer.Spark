using MintPlayer.Spark.Challenges.Contracts;

namespace MintPlayer.Spark.Challenges;

/// <summary>
/// One human-verification provider (commerce PRD §4.12, D29; research §6.4–§6.5): ALTCHA (built in), or an adapter
/// package (<c>MintPlayer.Spark.Challenges.Turnstile</c>, <c>.HCaptcha</c>, <c>.Recaptcha</c>, <c>.FriendlyCaptcha</c>),
/// or the host's own through <c>UseChallenge&lt;T&gt;()</c>. Registered as a singleton.
/// </summary>
/// <remarks>
/// A provider is called through <see cref="ISparkHumanChallenges"/>, which picks it, gates it on consent, refuses a
/// replayed response and switches the points on and off. A provider only offers and verifies; it never logs the
/// response or its secret.
/// </remarks>
public interface ISparkHumanChallenge
{
    /// <summary>The provider's name, lower case (<c>altcha</c>, <c>turnstile</c>): the client picks its widget by it.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the provider processes data on a third party's servers (its widget collects device signals), so it may run
    /// only with the visitor's consent for <c>third-party-challenge</c> (GA9, ePrivacy Art. 5(3)). Registering such a
    /// provider declares the requirement, so startup refuses it without the consent primitive (X21); a visitor who has not
    /// consented is offered ALTCHA instead, never blocked.
    /// </summary>
    bool RequiresConsent { get; }

    /// <summary>What the client needs to show the challenge.</summary>
    Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies a response. Never throws for a bad response: it answers a failed result with a reason.</summary>
    Task<SparkChallengeResult> VerifyAsync(SparkChallengeRequest request, string response, CancellationToken cancellationToken);

    /// <summary>
    /// The key a used response is remembered under, so a replay is refused (research §6.10: a used-response marker under
    /// lazy expiry). ALTCHA keys on its challenge; a hosted provider on the token itself.
    /// </summary>
    string ReplayKey(string response) => SparkChallengeHashing.Sha256Hex(response);
}

/// <summary>Where a challenge is asked.</summary>
/// <param name="Point">A <see cref="SparkChallengePoints"/> value.</param>
/// <param name="IdentityKey">The caller's identity key (X1), when known.</param>
/// <param name="RemoteAddress">The caller's address, passed on to a hosted provider's verification when it accepts one.</param>
/// <param name="Action">What the challenge protects (a room id), for providers that bind tokens to an action.</param>
public sealed record SparkChallengeRequest(string Point, string? IdentityKey = null, string? RemoteAddress = null, string? Action = null);

/// <summary>The outcome of a verification.</summary>
/// <param name="Passed">Whether the response proves a solved challenge.</param>
/// <param name="Provider">The provider that verified it.</param>
/// <param name="Error">Why it failed: <c>missing</c>, <c>invalid</c>, <c>expired</c>, <c>replayed</c>, <c>consent</c>, <c>wrong-provider</c>, <c>unavailable</c>, or the provider's own error code.</param>
/// <param name="Score">A provider's score (reCAPTCHA v3, hCaptcha Enterprise), 0 = bot … 1 = human, when it gives one.</param>
public sealed record SparkChallengeResult(bool Passed, string Provider, string? Error = null, double? Score = null)
{
    public static SparkChallengeResult Pass(string provider, double? score = null) => new(true, provider, null, score);

    public static SparkChallengeResult Fail(string provider, string error) => new(false, provider, error);
}

/// <summary>
/// The challenge points (research §6.10 "Where challenges apply"). Each can be switched on and off at runtime
/// (<see cref="SparkChallengesOptions.Points"/>, read through <see cref="ISparkChallengeSwitch"/>).
/// </summary>
public static class SparkChallengePoints
{
    /// <summary>Ballot registration: once per registration.</summary>
    public const string Register = "register";

    /// <summary>Joining a waiting room: once per identity per room, during the pre-queue (the main point of use).</summary>
    public const string QueueJoin = "queue-join";

    /// <summary>A hold: as a step-up only, when risk reaches <see cref="SparkChallengesOptions.StepUpScore"/>, through the 449 retry.</summary>
    public const string Hold = "hold";

    /// <summary>Checkout. Prefer the payment provider's own friction (3-D Secure) there.</summary>
    public const string Checkout = "checkout";

    /// <summary>Any other form a host protects.</summary>
    public const string Form = "form";

    /// <summary>
    /// Admission out of a waiting room. <b>Never</b> challenged: the queue position is the proof. Asking for it is refused.
    /// </summary>
    public const string Admission = "admission";

    /// <summary>The points that may be switched on.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Register, QueueJoin, Hold, Checkout, Form };
}

/// <summary>Hashing helpers shared by the providers.</summary>
public static class SparkChallengeHashing
{
    /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
