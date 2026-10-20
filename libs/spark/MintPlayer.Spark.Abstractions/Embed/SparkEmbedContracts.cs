using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.Embed;

/// <summary>
/// What an embedded page needs to know (<c>GET /spark/embed/config</c>, commerce PRD X23): the origins allowed to frame
/// it, which are also the only targets of its <c>postMessage</c> calls.
/// </summary>
/// <param name="AllowedOrigins">Exact origins (<c>https://organiser.example</c>) and wildcard ones (<c>https://*.organiser.example</c>).</param>
/// <param name="PathPrefixes">The SPA routes that may be framed (<c>/embed</c>).</param>
[SparkContract]
public sealed record SparkEmbedConfig(IReadOnlyList<string> AllowedOrigins, IReadOnlyList<string> PathPrefixes);

/// <summary>
/// A hand-over from an embed to a top-level window (<c>POST /spark/embed/handover</c>): the platform page to open and the
/// state it needs, because the visitor cookie (X1) is a third-party cookie inside an embed and may be blocked.
/// </summary>
/// <param name="Path">A path on the platform (<c>/checkout</c>); never another origin.</param>
/// <param name="Payload">Small JSON state (the selected offers); at most 2 KB.</param>
/// <param name="ReturnUrl">Where the top-level page may send the visitor back: an absolute URL on an allowed embedding origin.</param>
[SparkContract]
public sealed record SparkEmbedHandoverRequest(string Path, JsonElement? Payload = null, string? ReturnUrl = null);

/// <summary>The top-level URL of a hand-over, carrying a signed, short-lived token.</summary>
[SparkContract]
public sealed record SparkEmbedHandover(string Url, DateTimeOffset ExpiresAt);

/// <summary>A hand-over token to redeem (<c>POST /spark/embed/handover/redeem</c>), on the top-level page.</summary>
[SparkContract]
public sealed record SparkEmbedRedeemRequest(string Token);

/// <summary>What a redeemed hand-over carried.</summary>
[SparkContract]
public sealed record SparkEmbedHandoverState(string Path, JsonElement? Payload, string? ReturnUrl);
