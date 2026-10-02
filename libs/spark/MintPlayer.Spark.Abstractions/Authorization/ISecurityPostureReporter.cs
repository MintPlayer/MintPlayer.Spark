namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Describes what the running application's authorization configuration actually permits, so the
/// answer is visible without reading <c>security.json</c> and reasoning about group resolution.
/// <para>
/// Declared here rather than in the authorization package so <c>MintPlayer.Spark</c> can print the
/// summary without referencing it. An application with no authorization package registers no
/// reporter and nothing is printed — there is no posture to describe.
/// </para>
/// </summary>
public interface ISecurityPostureReporter
{
    /// <summary>
    /// The current posture. Computed from configuration alone — no database, no request — so the
    /// same call serves both the startup log and a CI gate.
    /// </summary>
    SecurityPosture Describe();
}

/// <summary>
/// What an anonymous caller can reach, plus anything about the configuration that deserves saying
/// out loud.
/// </summary>
/// <param name="AnonymouslyReachable">
/// Every right an unauthenticated caller holds, as <c>action/target</c> strings, sorted. Empty means
/// exactly that: nothing.
/// </param>
/// <param name="Warnings">
/// Configuration-level postures worth naming — an <c>AllowAll</c> default, an anonymous-access
/// override. Not errors: an application is entitled to be a public API. The point is that it should
/// be entitled to it <em>on purpose</em>.
/// </param>
/// <param name="Notes">
/// Information-level observations — today the stale attribute-deny finding (a group restricts a verb
/// on some attributes of a type, and others still inherit the type-level grant). Logged at
/// Information, never part of the <see cref="Fingerprint"/>.
/// </param>
public sealed record SecurityPosture(
    IReadOnlyList<string> AnonymouslyReachable,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Notes = null)
{

    /// <summary>
    /// A stable, order-independent rendering of the anonymous surface — what a CI gate compares
    /// against a committed baseline so that widening it shows up in a diff instead of in production.
    /// </summary>
    public string Fingerprint => string.Join("\n", AnonymouslyReachable);
}
