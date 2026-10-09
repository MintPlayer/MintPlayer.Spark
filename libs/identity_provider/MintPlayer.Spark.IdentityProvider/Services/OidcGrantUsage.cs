using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// When a grant was last used (D6: "last used" on the connected-applications page), recorded at most
/// once an hour, in a session of its own after the tokens were saved: the token batch runs under
/// optimistic concurrency, and a consent written meanwhile must not fail a token request.
/// </summary>
internal static class OidcGrantUsage
{
    public static async Task TouchAsync(IDocumentStore store, string? grantId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(grantId))
            return;

        using var session = store.OpenAsyncSession();
        var grant = await session.LoadAsync<OidcGrant>(grantId, ct);
        var now = DateTime.UtcNow;
        if (grant is null || grant.LastUsedAt > now.AddHours(-1))
            return;

        grant.LastUsedAt = now;
        try { await session.SaveChangesAsync(ct); }
        catch (Raven.Client.Exceptions.ConcurrencyException) { /* bookkeeping only */ }
    }
}
