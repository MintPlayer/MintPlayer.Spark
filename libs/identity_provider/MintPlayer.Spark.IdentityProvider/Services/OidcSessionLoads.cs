using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>Batch loads keyed the way the identity provider looks them up (#536 D47).</summary>
internal static class OidcSessionLoads
{
    /// <summary>
    /// The documents of <paramref name="ids"/> that exist, keyed ignoring case: a relational store answers ids in lower
    /// case, and a token or grant stores an application id as written (<c>OidcApplications/…</c> before #536 D7).
    /// </summary>
    public static async Task<Dictionary<string, T>> LoadExistingAsync<T>(this ISparkSession session, IEnumerable<string?> ids, CancellationToken ct)
        where T : class
    {
        var wanted = ids.OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return result;
        foreach (var (id, document) in await session.LoadAsync<T>(wanted, ct))
            if (document is not null)
                result[id] = document;
        return result;
    }

    /// <summary>
    /// The valid grants <paramref name="subject"/> holds, for display. Read from <see cref="OidcGrants_BySubject"/>, then
    /// loaded and checked exactly: never an authorization decision (see the view).
    /// </summary>
    public static async Task<List<OidcGrant>> ValidGrantsOfSubjectAsync(this ISparkSession session, string subject, CancellationToken ct)
    {
        var rows = await session.ToListAsync(session.Query<OidcGrantState, OidcGrants_BySubject>()
            .Where(g => g.Subject == subject), ct);
        var grants = await session.LoadExistingAsync<OidcGrant>(rows.Select(r => r.Id), ct);
        return [.. grants.Values.Where(g => g.Status == "valid" && string.Equals(g.Subject, subject, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The applications <paramref name="userId"/> is an active Admin of. Read from <see cref="OidcApplications_ByMember"/>,
    /// then loaded and checked exactly.
    /// </summary>
    public static async Task<List<OidcApplication>> ApplicationsAdministeredByAsync(this ISparkSession session, string userId, CancellationToken ct)
    {
        var rows = await session.ToListAsync(session.Query<OidcApplicationMemberEntry, OidcApplications_ByMember>()
            .Where(m => m.UserId == userId && m.Role == OidcMemberRoles.Admin && m.Status == OidcMemberStatuses.Active), ct);
        var apps = await session.LoadExistingAsync<OidcApplication>(rows.Select(r => r.Id), ct);
        return [.. apps.Values.Where(a => a.Members.Any(m => string.Equals(m.UserId, userId, StringComparison.Ordinal)
            && m.Role == OidcMemberRoles.Admin && m.Status == OidcMemberStatuses.Active))];
    }

    /// <summary>
    /// The applications with a scope awaiting approval. Read from <see cref="OidcApplications_ByScope"/>, then loaded and
    /// checked exactly.
    /// </summary>
    public static async Task<List<OidcApplication>> ApplicationsWithPendingScopesAsync(this ISparkSession session, CancellationToken ct)
    {
        var rows = await session.ToListAsync(session.Query<OidcApplicationScopeEntry, OidcApplications_ByScope>()
            .Where(s => s.Status == OidcScopeStatuses.Pending), ct);
        var apps = await session.LoadExistingAsync<OidcApplication>(rows.Select(r => r.Id), ct);
        return [.. apps.Values.Where(a => a.Scopes.Any(s => s.Status == OidcScopeStatuses.Pending))];
    }

    /// <summary>
    /// The applications whose go-live review awaits a decision. Read from <see cref="OidcApplications_WithGoLiveReview"/>,
    /// then loaded and checked exactly.
    /// </summary>
    public static async Task<List<OidcApplication>> ApplicationsAwaitingGoLiveAsync(this ISparkSession session, CancellationToken ct)
    {
        var rows = await session.ToListAsync(session.Query<OidcApplicationReference, OidcApplications_WithGoLiveReview>(), ct);
        var apps = await session.LoadExistingAsync<OidcApplication>(rows.Select(r => r.Id), ct);
        return [.. apps.Values.Where(a => a.GoLiveReview?.Status == OidcScopeStatuses.Pending)];
    }
}
