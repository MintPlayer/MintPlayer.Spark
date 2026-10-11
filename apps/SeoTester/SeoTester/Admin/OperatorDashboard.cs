using MintPlayer.Spark.Dashboards;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using SeoTester.Entities;
using SeoTester.LookupReferences;

namespace SeoTester.Admin;

/// <summary>
/// The operator dashboard (<c>docs/seo_analyzer_PRD.md</c> F16, D28) is a package dashboard (#504, D13): a live virtual
/// persistent object (<c>App_Data/Model/OperatorDashboard.json</c>, no documents) that the dashboards package loads,
/// pushes and draws as tiles. <c>Read/OperatorDashboard</c> is granted to <c>dashboards:administrators</c>, which
/// <c>security.json</c> binds to Administrators; its figures are <see cref="OperatorDashboardFigures"/> plus the
/// package's RavenDB statistics, and its page lists the abuse log (the <c>audit-log</c> query) under the tiles.
/// </summary>
public sealed class OperatorDashboardActions(IServiceProvider services) : SparkDashboardActions(services)
{
    public const string TypeName = "OperatorDashboard";
}

/// <summary>
/// Usage, abuse and site figures of the operator dashboard. Read with a raw session on purpose: the operator sees every
/// organization's sites (D28), which no row policy would show. The database's size and collections come from the
/// package's built-in RavenDB statistics.
/// </summary>
internal sealed class OperatorDashboardFigures(IDocumentStore store, TimeProvider time) : IDashboardProvider
{
    /// <summary>The sites listed, newest first; the counts cover all of them.</summary>
    public const int MaxSites = 500;

    public async Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var dayAgo = now.AddDays(-1);
        var weekAgo = now.AddDays(-7);

        var collections = await store.Maintenance.ForDatabase(store.Database).SendAsync(new GetDetailedCollectionStatisticsOperation(), cancellationToken);
        long Count(string collection) => collections.Collections.TryGetValue(collection, out var c) ? c.CountOfDocuments : 0;

        using var session = store.OpenAsyncSession();
        var verifiedSites = await session.Query<Site>().CountAsync(s => s.IsVerified, cancellationToken);
        var auditsLastDay = await session.Query<AuditRun>().CountAsync(r => r.StartedAtUtc >= dayAgo, cancellationToken);
        var weekRuns = await session.Query<AuditRun>().Where(r => r.StartedAtUtc >= weekAgo).Select(r => r.Status).Take(10_000).ToListAsync(cancellationToken);
        var running = await session.Query<AuditRun>().CountAsync(r => r.Status.In(AuditRunStatus.InProgress), cancellationToken);
        var refusals = await session.Query<AuditLogEntry>()
            .Where(e => e.AtUtc >= weekAgo && e.Kind.In(AuditLogKinds.QuotaRefused, AuditLogKinds.CoolDownRefused, AuditLogKinds.GateRefused))
            .Select(e => e.Kind).Take(10_000).ToListAsync(cancellationToken);

        values
            .Set("Users", Count("SparkUsers"))
            .Set("Organizations", Count("Organizations"))
            .Set("Sites", Count("Sites"))
            .Set("VerifiedSites", verifiedSites)
            .Set("Audits", Count("AuditRuns"))
            .Set("AuditsLastDay", auditsLastDay)
            .Set("AuditsLastWeek", weekRuns.Count)
            .Set("Running", running)
            .Set("AuditsByStatus", weekRuns.GroupBy(s => s).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()))
            .Set("Refusals", refusals.GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));

        var sites = await session.Query<Site>().OrderByDescending(s => s.CreatedAtUtc).Take(MaxSites).ToListAsync(cancellationToken);
        var organizationIds = sites.Select(s => s.OrganizationId).OfType<string>().Distinct().ToArray();
        var organizations = organizationIds.Length > 0 ? await session.LoadAsync<Organization>(organizationIds, cancellationToken) : new Dictionary<string, Organization>();
        values.Set("SiteList", sites.Select(s =>
        {
            var organization = s.OrganizationId is { } o && organizations.TryGetValue(o, out var org) ? org : null;
            return new Dictionary<string, object?>
            {
                ["origin"] = s.Origin,
                ["organization"] = organization?.Name,
                ["personal"] = organization is { IsPersonal: true },
                ["verified"] = s.IsVerified,
                ["score"] = s.LatestScore is null ? null : $"{s.LatestScore} {s.LatestGrade}".TrimEnd(),
                ["latestRunAtUtc"] = s.LatestRunAtUtc,
                ["createdAtUtc"] = s.CreatedAtUtc,
            };
        }).ToList());
    }
}
