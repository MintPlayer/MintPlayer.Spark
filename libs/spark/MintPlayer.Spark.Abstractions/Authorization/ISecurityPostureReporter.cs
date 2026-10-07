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
/// <param name="Rights">
/// Every effective right, one row each, sorted (composition D4): the table <c>securityPosture.txt</c>
/// commits, so a library added or updated shows up as a diff in the pull request that brings it.
/// </param>
/// <param name="Inert">The rights of libraries switched off in <c>"libraries"</c>: listed, never applied.</param>
/// <param name="Layers">Every library that ships a <c>security.json</c> layer, with the hash of what it states (composition D7).</param>
public sealed record SecurityPosture(
    IReadOnlyList<string> AnonymouslyReachable,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Notes = null,
    IReadOnlyList<SecurityPostureRow>? Rights = null,
    IReadOnlyList<SecurityPostureRow>? Inert = null,
    IReadOnlyList<SecurityPostureLayer>? Layers = null)
{

    /// <summary>
    /// A stable, order-independent rendering of the anonymous surface — what a CI gate compares
    /// against a committed baseline so that widening it shows up in a diff instead of in production.
    /// </summary>
    public string Fingerprint => string.Join("\n", AnonymouslyReachable);
}

/// <summary>One row of the posture table: who, what, which resource, under which key, from which layer.</summary>
/// <param name="Group">The group's name (its id when it has none); a right named by token or slot adds it, <c>Signed-in users (@authenticated)</c>. An inert row shows only the token.</param>
/// <param name="Effect"><c>grant</c> or <c>deny</c>, with <c>important</c> for the precedence tier.</param>
/// <param name="Resource">The resource as written, not expanded.</param>
/// <param name="Key">The right's key in the composed set.</param>
/// <param name="Layer"><c>app</c>, or the alias of the library that ships it.</param>
/// <param name="Anonymous">Granted to the anonymous group: the posture file lists these in their own section.</param>
public sealed record SecurityPostureRow(string Group, string Effect, string Resource, string Key, string Layer, bool Anonymous = false);

/// <summary>A library that ships rights, as the posture table names it (composition D7).</summary>
/// <param name="Alias">Its alias: the layer column of its rows.</param>
/// <param name="Assembly">Its assembly.</param>
/// <param name="Hash">The first 12 hex digits of the SHA-256 of its <c>security.json</c> layer, whitespace aside: an update that changes what it states changes this line.</param>
/// <param name="SwitchedOff">Switched off in <c>"libraries"</c>: its rights are listed as inert.</param>
public sealed record SecurityPostureLayer(string Alias, string Assembly, string Hash, bool SwitchedOff);
