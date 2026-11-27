using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// What disabling an application or an API resource takes down with it (<c>docs/identity_provider_platform_PRD.md</c>
/// D9, G26). Without it, <c>Enabled = false</c> stopped new sign-ins while every token already issued kept working
/// until it expired.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>An application:</b> every valid token it holds (codes, access and refresh tokens, device codes, pushed
/// requests) and every grant its users gave it. Re-enabling it does not bring them back: its users consent again.</item>
/// <item><b>An API resource:</b> every valid token carrying one of its scopes. Grants stay: a disabled scope is no
/// longer issued, and re-enabling the resource honours the consent the users already gave.</item>
/// </list>
/// The rows come from a view (<see cref="OidcTokens_ByState"/>, <see cref="OidcTokens_ByScope"/>,
/// <see cref="OidcGrants_BySubject"/>; #536 D47), read once the view has caught up (bounded), and the status is set
/// set-based by id: on RavenDB a patch of <c>id() in (…)</c>, which needs no index. The access-token check
/// (<see cref="AccessTokens"/>) reads the stored status, not this call.
/// </remarks>
internal static class OidcDisableCascade
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>How many ids one set-based write names.</summary>
    private const int Batch = 256;

    public static async Task RevokeApplicationAsync(ISparkStore store, string applicationId, CancellationToken ct = default)
    {
        using var session = store.OpenSession();
        var tokens = await session.ToListAsync(session.Query<OidcTokenState, OidcTokens_ByState>(Wait)
            .Where(t => t.ApplicationId == applicationId && t.Status == "valid"), ct);
        await RevokeTokensAsync(session, tokens.Select(t => t.Id), ct);

        var grants = await session.ToListAsync(session.Query<OidcGrantState, OidcGrants_BySubject>(Wait)
            .Where(g => g.ApplicationId == applicationId && g.Status == "valid"), ct);
        await RevokeGrantsAsync(session, grants.Select(g => g.Id), ct);
    }

    public static async Task RevokeScopesAsync(ISparkStore store, IReadOnlyCollection<string> scopes, CancellationToken ct = default)
    {
        if (scopes.Count == 0)
            return;
        var wanted = scopes.ToList();
        using var session = store.OpenSession();
        var tokens = await session.ToListAsync(session.Query<OidcTokenScope, OidcTokens_ByScope>(Wait)
            .Where(t => t.Status == "valid" && wanted.Contains(t.Scope)), ct);
        await RevokeTokensAsync(session, tokens.Select(t => t.Id), ct);
    }

    /// <summary>Sets <c>Status = "revoked"</c> on the tokens named by <paramref name="ids"/>.</summary>
    internal static async Task RevokeTokensAsync(ISparkSession session, IEnumerable<string?> ids, CancellationToken ct)
    {
        foreach (var chunk in Chunks(ids))
        {
            var keys = chunk.Select(Id<OidcToken>.FromTrusted).ToList();
            await session.UpdateWhereAsync<OidcToken>(t => keys.Contains(t.Id), set => set.Set(t => t.Status, "revoked"), ct);
        }
    }

    private static async Task RevokeGrantsAsync(ISparkSession session, IEnumerable<string?> ids, CancellationToken ct)
    {
        foreach (var chunk in Chunks(ids))
        {
            var keys = chunk.Select(Id<OidcGrant>.FromTrusted).ToList();
            await session.UpdateWhereAsync<OidcGrant>(g => keys.Contains(g.Id), set => set.Set(g => g.Status, "revoked"), ct);
        }
    }

    private static IEnumerable<string[]> Chunks(IEnumerable<string?> ids)
        => ids.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Batch);
}
