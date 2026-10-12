using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// The user an agent token names, read from the store once per request: the token check (security stamp,
/// lockout) and the store-backed groups (PRD §4.6) share the read. Scoped, so nothing outlives the request:
/// a role removal or a stamp change applies to the next call.
/// </summary>
internal sealed class AgentRequestUser(IDocumentStore store)
{
    private (string Id, Task<SparkUser?> User)? loaded;

    public Task<SparkUser?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (loaded is { } hit && string.Equals(hit.Id, userId, StringComparison.Ordinal))
            return hit.User;

        var user = LoadAsync(userId, cancellationToken);
        loaded = (userId, user);
        return user;
    }

    private async Task<SparkUser?> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<SparkUser>(userId, cancellationToken);
    }

    /// <summary>Whether Identity would refuse the user's sign-in as locked out.</summary>
    public static bool IsLockedOut(SparkUser user, DateTimeOffset now)
        => user.LockoutEnabled && user.LockoutEnd is { } end && end > now;
}
