namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// The roles a group may be declared to play in <c>security.json</c>'s <c>wellKnown</c> block.
/// <para>
/// Public because the loader, the evaluator, the validator, the posture reporter and the starter
/// generator all have to agree on the spelling, and because an application writing the file by
/// hand — or a tool generating it — needs the same two tokens.
/// </para>
/// </summary>
public static class SparkWellKnownGroups
{
    /// <summary>
    /// A caller who has not signed in.
    /// <para>
    /// <b>Not the old <c>Everyone</c>.</b> A right that both an anonymous visitor and a signed-in
    /// user should have is two grants. That is verbose on purpose: the alternative was one token
    /// that quietly meant "the public internet", which is the whole of #298.
    /// </para>
    /// </summary>
    public const string Anonymous = "anonymous";

    /// <summary>Every caller who has signed in, whatever claims they carry.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>The recognised <c>wellKnown</c> keys. Anything else in that block is refused.</summary>
    public static readonly string[] All = [Anonymous, Authenticated];
}
