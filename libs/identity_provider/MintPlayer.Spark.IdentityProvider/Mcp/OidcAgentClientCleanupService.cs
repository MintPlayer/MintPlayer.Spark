using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The agent-client housekeeping of <c>idp.ForMcp()</c> (MCP PRD D5, plan M3b "cleanup job"): every six hours,
/// anonymously registered clients unused for <see cref="SparkIdentityProviderMcpOptions.UnusedClientLifetime"/> are
/// deleted, with their tokens, OAuth grants and agent grants.
/// </summary>
/// <remarks>
/// Expired authorization codes, requests, access and refresh tokens need no job: they carry <c>@expires</c>, so
/// RavenDB deletes them itself, and every read refuses an expired record whether or not it is gone yet
/// (<c>OidcExpiry</c>). A metadata-document client is not pruned: it is re-created from its document on its next
/// sign-in anyway, and it is only stored, never trusted, between fetches.
/// </remarks>
internal sealed partial class OidcAgentClientCleanupService(
    IDocumentStore store,
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
        List<OidcApplication> stale;
        using (var session = store.OpenAsyncSession())
        {
            // Housekeeping over an auto-index: a client the index has not seen yet is pruned next time.
            stale = await session.Query<OidcApplication>()
                .Where(a => a.AgentRegistration == OidcAgentRegistrations.DynamicRegistration && a.LastUsedAt < cutoff)
                .Take(500)
                .ToListAsync(ct);
        }

        var pruned = 0;
        foreach (var app in stale)
        {
            using var session = store.OpenAsyncSession();
            // Re-read: a sign-in since the query keeps the client.
            var current = await session.LoadAsync<OidcApplication>(app.Id, ct);
            if (current is null || current.LastUsedAt >= cutoff)
                continue;

            var subjects = (await session.Query<OidcGrant>()
                    // Bounded: a grant the index has not seen would keep its agent grant (which /mcp refuses anyway: no client).
                    .Customize(x => x.WaitForNonStaleResults(IndexWait))
                    .Where(g => g.ApplicationId == current.Id)
                    .Select(g => g.Subject)
                    .ToListAsync(ct))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            await OidcDisableCascade.RevokeApplicationAsync(store, current.Id!, ct);
            foreach (var subject in subjects)
                await agentGrants.RemoveAsync(subject, current.ClientId, ct);

            await OidcClientIdReservation.ReleaseAsync(session, current);
            session.Delete(current);
            await audit.RecordAsync(session, OidcAuditKinds.RegistrationPruned, actorId: null, current.Id,
                details: new Dictionary<string, string> { ["clientId"] = current.ClientId, ["lastUsedAt"] = current.LastUsedAt?.ToString("O") ?? "" }, ct: ct);
            await session.SaveChangesAsync(ct);
            pruned++;
        }

        return pruned;
    }

    [LoggerMessage(EventId = 7420, Level = LogLevel.Error, Message = "Pruning unused agent clients failed; it is retried in six hours.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
