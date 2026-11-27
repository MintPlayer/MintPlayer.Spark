using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The agent-client housekeeping of <c>idp.ForMcp()</c> (MCP PRD D5, plan M3b "cleanup job"): every six hours,
/// anonymously registered clients unused for <see cref="SparkIdentityProviderMcpOptions.UnusedClientLifetime"/> are
/// deleted, with their tokens, OAuth grants and agent grants.
/// </summary>
/// <remarks>
/// Expired authorization codes, requests, access and refresh tokens need no job: they carry an expiry, so the store
/// deletes them itself (<c>ISparkSession.SetExpiry</c>), and every read refuses an expired record whether or not it is gone yet
/// (<c>OidcExpiry</c>). A metadata-document client is not pruned: it is re-created from its document on its next
/// sign-in anyway, and it is only stored, never trusted, between fetches.
/// </remarks>
internal sealed partial class OidcAgentClientCleanupService(
    ISparkStore store,
    SparkIdentityProviderOptions options,
    OidcAgentGrants agentGrants,
    OidcAudit audit,
    ILogger<OidcAgentClientCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private static readonly TimeSpan IndexWait = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PruneAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
    }

    /// <summary>Deletes the dynamically registered clients last used before <paramref name="now"/> − the lifetime. Answers how many.</summary>
    public async Task<int> PruneAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now - (options.Mcp?.UnusedClientLifetime ?? TimeSpan.FromDays(30));
        List<string> stale;
        using (var session = store.OpenSession())
        {
            // Housekeeping over a view (#536 D47): a client the view has not seen yet is pruned next time.
            stale = [.. (await session.ToListAsync(session.Query<OidcAgentClientUse, OidcAgentClients_ByLastUse>()
                    .Where(a => a.AgentRegistration == OidcAgentRegistrations.DynamicRegistration && a.LastUsedAt < cutoff)
                    .Take(500), ct))
                .Select(a => a.Id).OfType<string>()];
        }

        var pruned = 0;
        foreach (var id in stale)
        {
            using var session = store.OpenSession();
            // Re-read: a sign-in since the query keeps the client, and the view compares the store's way.
            var current = await session.LoadAsync<OidcApplication>(id, ct);
            if (current is null || current.AgentRegistration != OidcAgentRegistrations.DynamicRegistration
                || current.LastUsedAt is not { } lastUsedAt || lastUsedAt >= cutoff)
                continue;

            // Bounded: a grant the view has not seen would keep its agent grant (which /mcp refuses anyway: no client).
            var grantRows = await session.ToListAsync(session.Query<OidcGrantState, OidcGrants_BySubject>(IndexWait)
                .Where(g => g.ApplicationId == current.Id.Value), ct);
            var subjects = (await session.LoadExistingAsync<OidcGrant>(grantRows.Select(g => g.Id), ct)).Values
                .Where(g => string.Equals(g.ApplicationId, current.Id.Value, StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Subject)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            await OidcDisableCascade.RevokeApplicationAsync(store, current.Id.Value, ct);
            foreach (var subject in subjects)
                await agentGrants.RemoveAsync(subject, current.ClientId, ct);

            OidcClientIdReservation.Release(session, current);
            session.Delete(current);
            await audit.RecordAsync(session, OidcAuditKinds.RegistrationPruned, actorId: null, current.Id.Value,
                details: new Dictionary<string, string> { ["clientId"] = current.ClientId, ["lastUsedAt"] = current.LastUsedAt?.ToString("O") ?? "" }, ct: ct);
            await session.SaveChangesAsync(ct);
            pruned++;
        }

        return pruned;
    }

    [LoggerMessage(EventId = 7420, Level = LogLevel.Error, Message = "Pruning unused agent clients failed; it is retried in six hours.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
