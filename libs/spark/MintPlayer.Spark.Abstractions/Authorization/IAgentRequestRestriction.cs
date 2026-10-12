namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Narrows the rights of the current request when it is made on an agent's behalf (MCP PRD D13): the
/// per-type restriction a user chose for an agent grant is applied inside
/// <see cref="IPermissionService"/>, so every caller — generic MCP tools, app-written tools, and the
/// framework code they call — honours it without asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Narrowing only.</b> <see cref="IPermissionService"/> asks a restriction only about a resource
/// <c>security.json</c> (or a share grant) already allows, and a <see langword="false"/> denies it; nothing
/// a restriction answers can allow a resource that was denied. With no restriction registered — every
/// application without the MCP package — the service behaves exactly as before.
/// </para>
/// <para>
/// Registered scoped (it answers for one request) and resolved per request. Several may be registered;
/// all of them must allow. Return <see langword="true"/> for a request that is not an agent's, so the
/// web is untouched.
/// </para>
/// </remarks>
public interface IAgentRequestRestriction
{
    /// <summary>
    /// Whether <paramref name="action"/> on <paramref name="target"/> stays allowed for the current
    /// request. Called only for a resource the caller's own rights already allow.
    /// </summary>
    /// <param name="action">The action, as <see cref="IPermissionService"/> received it (<c>Read</c>, <c>Query</c>, <c>Edit</c>, a custom action name…).</param>
    /// <param name="target">The target: an entity type name, or another resource name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> AllowsAsync(string action, string target, CancellationToken cancellationToken = default);
}
