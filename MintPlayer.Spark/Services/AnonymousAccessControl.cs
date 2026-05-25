using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Fail-closed default <see cref="IAccessControl"/> registered by <c>AddSpark</c>
/// when neither <c>spark.AddAuthorization()</c> nor <c>spark.AllowAnonymousAccess()</c>
/// has been called. Denies every check so the framework can't be silently open if
/// the developer forgets to wire authorization.
/// </summary>
internal sealed class DenyAllAccessControl : IAccessControl
{
    public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// Permissive <see cref="IAccessControl"/> registered by
/// <c>spark.AllowAnonymousAccess()</c>. Allows every check unconditionally —
/// use only for demos, prototypes, or apps that intentionally have no
/// authorization model. Opt-in so the intent is visible in code.
/// </summary>
internal sealed class AllowAllAccessControl : IAccessControl
{
    public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
