namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The body of a core endpoint's refusal that carries only a message, <c>{"error":"…"}</c> — e.g. the lookup-reference
/// endpoints' <c>{"error":"Operation failed"}</c>. Commerce X20 item 2: it replaced the anonymous objects and
/// serializes exactly as they did. The auth endpoints have their own <c>SparkAuthError</c> (an error code).
/// </summary>
/// <param name="Error">The message.</param>
[SparkContract("SparkError")]
public sealed record SparkErrorResponse(string Error);
