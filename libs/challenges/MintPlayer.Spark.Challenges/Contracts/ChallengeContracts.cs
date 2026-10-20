using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Challenges.Contracts;

// The wire shapes @mintplayer/ng-spark/challenges reads and writes (X20.2).

/// <summary>An ALTCHA challenge in the altcha-lib v1 shape (<c>algorithm</c>, <c>challenge</c>, <c>maxnumber</c>, <c>salt</c>, <c>signature</c>).</summary>
/// <param name="Algorithm">Always <c>SHA-256</c>.</param>
/// <param name="Challenge">Hex SHA-256 of <c>salt + number</c>; the client searches for <c>number</c>.</param>
/// <param name="Maxnumber">The upper bound of the search (the complexity).</param>
/// <param name="Salt">Random hex plus <c>?expires=&lt;unix seconds&gt;</c>.</param>
/// <param name="Signature">The server's seal over the challenge and the point; opaque to the client.</param>
[SparkContract]
public sealed record SparkAltchaChallenge(string Algorithm, string Challenge, long Maxnumber, string Salt, string Signature);

/// <summary>What a client needs to show the challenge for one point (<c>GET /spark/challenges/offer?point=</c>).</summary>
/// <param name="Provider">The provider name: <c>altcha</c>, <c>turnstile</c>, <c>hcaptcha</c>, <c>recaptcha</c>, <c>friendlycaptcha</c> or a host's own.</param>
/// <param name="Point">The challenge point (<c>queue-join</c>, <c>hold</c>, …).</param>
/// <param name="SiteKey">A hosted provider's public site key.</param>
/// <param name="ScriptUrl">A hosted provider's widget script, loaded only after consent (GA9).</param>
/// <param name="Altcha">The proof-of-work challenge, for <c>altcha</c>.</param>
/// <param name="ConsentFallback">True when a third-party provider is configured but the visitor has not consented, so ALTCHA is offered instead (GA9).</param>
/// <param name="Action">The action name a hosted provider binds the token to (reCAPTCHA v3, Turnstile), when it has one.</param>
[SparkContract]
public sealed record SparkChallengeOffer(
    string Provider,
    string Point,
    string? SiteKey = null,
    string? ScriptUrl = null,
    SparkAltchaChallenge? Altcha = null,
    bool ConsentFallback = false,
    string? Action = null);

/// <summary>A solved challenge as the client sends it back.</summary>
/// <param name="Provider">The provider that produced <paramref name="Payload"/>.</param>
/// <param name="Payload">ALTCHA: the base64 JSON solution; a hosted provider: its response token.</param>
[SparkContract]
public sealed record SparkChallengeResponse(string Provider, string Payload);
