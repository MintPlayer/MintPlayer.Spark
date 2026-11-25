using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The IdentityProvider's contribution to the account page's <b>Connected applications</b> (MCP PRD D14): the user's
/// sign-ins at other websites through this provider (OAuth grants that are not agent grants), so one page lists
/// everything the user allowed. Agent clients are left out: their <c>AgentGrant</c> is listed already, and revoking it
/// revokes the OAuth grant too (D12). <c>/connect/applications</c> and the SPA portal page stay as they are.
/// </summary>
internal sealed class OidcConnectedApplicationSource(ISparkStore store,OidcGrantWithdrawal withdrawal, ConnectText text) : ISparkConnectedApplicationSource
{
    public string Name => "identity-provider";

    public async Task<IReadOnlyList<SparkConnectedApplication>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenSession();
        // A list, not a decision: the view's few seconds of staleness decide nothing (revocation loads).
        var grants = await session.ValidGrantsOfSubjectAsync(userId, cancellationToken);
        var apps = await session.LoadExistingAsync<OidcApplication>(grants.Select(g => g.ApplicationId), cancellationToken);
        var definitions = await OidcScopeCatalog.LoadAsync(session, grants.SelectMany(g => g.GrantedScopes), cancellationToken);

        var result = new List<SparkConnectedApplication>();
        foreach (var grant in grants.OrderBy(g => g.CreatedAt))
        {
            if (!apps.TryGetValue(grant.ApplicationId, out var app) || OidcAgentClients.IsAgent(app)
                || grant.GrantedScopes.Any(s => s is OidcAgentClients.ReadScope or OidcAgentClients.WriteScope))
                continue;

            var scopes = string.Join(", ", grant.GrantedScopes.Select(name =>
                text.Of(definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.DisplayName, name)));
            result.Add(new SparkConnectedApplication(
                grant.ApplicationId, app.DisplayName, scopes,
                new DateTimeOffset(DateTime.SpecifyKind(grant.CreatedAt, DateTimeKind.Utc)),
                grant.LastUsedAt is { } used ? new DateTimeOffset(DateTime.SpecifyKind(used, DateTimeKind.Utc)) : null));
        }
        return result;
    }

    public async Task<bool> RevokeAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        // The id is the application's; the grant is addressed through the signed-in user, so no other user's can be named.
        using (var session = store.OpenSession())
        {
            var grant = await session.LoadAsync<OidcGrant>(OidcGrantReference.DocumentId(userId, id), cancellationToken);
            if (grant is not { Status: "valid" } || !string.Equals(grant.Subject, userId, StringComparison.Ordinal))
                return false;
        }
        return await withdrawal.WithdrawAsync(userId, id, scopes: null, ipAddress: null, cancellationToken);
    }
}
