namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// The extension point of the account page's <b>Connected applications</b> (MCP PRD D14): another package
/// contributes the access it granted on the user's behalf that is <i>not</i> an agent grant — the
/// IdentityProvider's sign-ins at other websites (OAuth grants) — so the user sees and revokes everything in one
/// place. The IdentityProvider references this package, not the reverse, hence a registration rather than a
/// call. Register as many as needed (<c>services.AddScoped&lt;ISparkConnectedApplicationSource, T&gt;()</c>).
/// </summary>
public interface ISparkConnectedApplicationSource
{
    /// <summary>Identifies the source in requests (<c>"identity-provider"</c>); stable, lower-case.</summary>
    string Name { get; }

    /// <summary>What the user granted through this source.</summary>
    Task<IReadOnlyList<SparkConnectedApplication>> ListAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Revokes the user's entry <paramref name="id"/>; <see langword="false"/> when the user has none with that id.</summary>
    Task<bool> RevokeAsync(string userId, string id, CancellationToken cancellationToken = default);
}

/// <summary>One contributed entry of Connected applications.</summary>
/// <param name="Id">The source's id of the entry, sent back to revoke it.</param>
/// <param name="Name">What the user recognises it by (the application's name).</param>
/// <param name="Description">Optional detail, e.g. the granted scopes, in the request's language.</param>
/// <param name="CreatedAt">When access was first granted.</param>
/// <param name="LastUsedAt">When it was last used, if known.</param>
public sealed record SparkConnectedApplication(string Id, string Name, string? Description, DateTimeOffset? CreatedAt, DateTimeOffset? LastUsedAt);
