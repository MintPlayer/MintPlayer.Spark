using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Organizations.Services;

/// <summary>
/// A user's memberships as <c>organization id → role</c>, read once per request (scoped) and shared by
/// every type the row policy filters (§12 "Spike S13", design (a)).
/// </summary>
/// <remarks>
/// <para>
/// Read by <b>id prefix</b> (<see cref="OrganizationMembership.PrefixFor"/>), straight from storage: no
/// index, so a membership saved a moment ago (an invitation just accepted) or deleted (a member just
/// removed) counts on the very next request. A query on <c>UserId</c> would ride an auto-index and could
/// show a removed member the organization's rows for as long as the index lags.
/// </para>
/// <para>
/// The package drops the cached entry whenever it changes a membership in the same request
/// (<see cref="Invalidate"/>), so an organization created in a request is readable in its response.
/// </para>
/// </remarks>
internal sealed class OrganizationMembershipReader(IDocumentStore store)
{
    /// <summary>Memberships are paged in batches of this size; a user in more organizations takes more round trips.</summary>
    internal const int PageSize = 1024;

    private readonly Dictionary<string, IReadOnlyDictionary<string, OrganizationRole>> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="userId"/>'s memberships, from this request's cache or storage.</summary>
    public async Task<IReadOnlyDictionary<string, OrganizationRole>> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(userId, out var cached))
            return cached;

        using var session = store.OpenAsyncSession();
        var memberships = await LoadAsync(session, userId, cancellationToken);
        var roles = memberships
            .Where(m => !string.IsNullOrEmpty(m.OrganizationId))
            .ToDictionary(m => m.OrganizationId!, m => m.Role, StringComparer.OrdinalIgnoreCase);
        cache[userId] = roles;
        return roles;
    }

    /// <summary>Forgets <paramref name="userId"/>'s cached memberships (every user's when null).</summary>
    public void Invalidate(string? userId = null)
    {
        if (userId is null)
            cache.Clear();
        else
            cache.Remove(userId);
    }

    /// <summary>Every membership document of <paramref name="userId"/>, by id prefix, through <paramref name="session"/>.</summary>
    public static async Task<List<OrganizationMembership>> LoadAsync(IAsyncDocumentSession session, string userId, CancellationToken cancellationToken)
    {
        var prefix = OrganizationMembership.PrefixFor(userId);
        var all = new List<OrganizationMembership>();
        for (var start = 0; ; start += PageSize)
        {
            var page = await session.Advanced.LoadStartingWithAsync<OrganizationMembership>(prefix, start: start, pageSize: PageSize, token: cancellationToken);
            var batch = page.ToList();
            all.AddRange(batch);
            if (batch.Count < PageSize)
                return all;
        }
    }
}
