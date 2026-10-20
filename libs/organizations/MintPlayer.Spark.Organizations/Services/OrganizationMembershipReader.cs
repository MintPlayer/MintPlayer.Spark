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

    private readonly Dictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One user's memberships: organization id → built-in role, and → custom roles (X8).</summary>
    private sealed record Entry(IReadOnlyDictionary<string, OrganizationRole> Roles, IReadOnlyDictionary<string, IReadOnlyList<string>> CustomRoles);

    /// <summary><paramref name="userId"/>'s memberships, from this request's cache or storage.</summary>
    public async Task<IReadOnlyDictionary<string, OrganizationRole>> GetAsync(string userId, CancellationToken cancellationToken = default)
        => (await GetEntryAsync(userId, cancellationToken)).Roles;

    /// <summary><paramref name="userId"/>'s custom roles per organization (<c>docs/commerce_PRD.md</c> X8); an organization without any is absent.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetCustomRolesAsync(string userId, CancellationToken cancellationToken = default)
        => (await GetEntryAsync(userId, cancellationToken)).CustomRoles;

    /// <summary>The organizations in which <paramref name="userId"/> meets <paramref name="requirement"/>.</summary>
    public async Task<string[]> OrganizationsMeetingAsync(string userId, OrganizationRequirement requirement, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(userId, cancellationToken);
        return entry.Roles
            .Where(r => requirement.IsMetBy(r.Value, entry.CustomRoles.GetValueOrDefault(r.Key)))
            .Select(r => r.Key)
            .ToArray();
    }

    /// <summary>Whether <paramref name="userId"/> meets <paramref name="requirement"/> in <paramref name="organizationId"/>.</summary>
    public async Task<bool> MeetsAsync(string userId, string organizationId, OrganizationRequirement requirement, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(userId, cancellationToken);
        return entry.Roles.TryGetValue(organizationId, out var role)
            && requirement.IsMetBy(role, entry.CustomRoles.GetValueOrDefault(organizationId));
    }

    private async Task<Entry> GetEntryAsync(string userId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(userId, out var cached))
            return cached;

        using var session = store.OpenAsyncSession();
        var memberships = (await LoadAsync(session, userId, cancellationToken))
            .Where(m => !string.IsNullOrEmpty(m.OrganizationId))
            .ToList();
        var entry = new Entry(
            memberships.ToDictionary(m => m.OrganizationId!, m => m.Role, StringComparer.OrdinalIgnoreCase),
            memberships
                .Where(m => m.CustomRoles is { Count: > 0 })
                .ToDictionary(m => m.OrganizationId!, m => (IReadOnlyList<string>)m.CustomRoles.ToArray(), StringComparer.OrdinalIgnoreCase));
        cache[userId] = entry;
        return entry;
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
