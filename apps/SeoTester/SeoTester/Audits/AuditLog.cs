using MintPlayer.Spark.Settings;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.Settings;

namespace SeoTester.Audits;

/// <summary>
/// Writes the abuse log (<c>docs/seo_analyzer_PRD.md</c> D28, D37): one <see cref="AuditLogEntry"/> per event, expiring
/// after <see cref="SeoSettings.AuditLogRetention"/>. Its own session and commit, so a refusal is logged even though the
/// request then fails.
/// </summary>
public sealed class AuditLog(IDocumentStore store, ISparkSettings settings, TimeProvider time)
{
    public async Task WriteAsync(string kind, string? userId, Site site, string? runId, string? detail, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(await EntryAsync(kind, userId, site, runId, detail, cancellationToken), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>An entry to store in the caller's own unit of work.</summary>
    public async Task<AuditLogEntry> EntryAsync(string kind, string? userId, Site site, string? runId, string? detail, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return new AuditLogEntry
        {
            AtUtc = now,
            Kind = kind,
            UserId = userId,
            OrganizationId = site.OrganizationId,
            SiteId = site.Id,
            Origin = site.Origin,
            RunId = runId,
            Verified = site.IsVerified,
            Detail = detail is { Length: > 500 } ? detail[..500] : detail,
            ExpiresAtUtc = now + await settings.GetAsync(SeoSettings.AuditLogRetention, cancellationToken),
        };
    }
}
