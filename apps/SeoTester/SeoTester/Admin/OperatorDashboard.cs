using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using SeoTester.Entities;
using SeoTester.LookupReferences;

namespace SeoTester.Admin;

/// <summary>
/// The operator dashboard (<c>docs/seo_analyzer_PRD.md</c> F16, D28) is a virtual persistent object
/// (<c>App_Data/Model/OperatorDashboard.json</c>, no documents): its menu entry and page are gated by
/// <c>Read/OperatorDashboard</c> (Administrators, <c>security.json</c>), its page lists the abuse log (the
/// <c>audit-log</c> query), and the SPA draws the numbers from <see cref="OperatorDashboardEndpoint"/>.
/// </summary>
public partial class OperatorDashboardActions
{
    public const string TypeName = "OperatorDashboard";

    [Inject] private readonly IManager manager;

    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = await manager.GetPersistentObjectAsync(TypeName);
        obj.Breadcrumb = manager.GetTranslatedMessage("seo.dashboardTitle");
        return obj;
    }
}

/// <summary>
/// <c>GET /seotester/admin/dashboard</c>: usage, abuse and storage figures for whoever may open the operator dashboard
/// (<c>Read/OperatorDashboard</c>); anyone else gets 404. Read with a raw session on purpose: the operator sees every
/// organization's sites (D28), which no row policy would show.
/// </summary>
internal sealed partial class OperatorDashboardEndpoint : IGetEndpoint
{
    public const string Route = "/seotester/admin/dashboard";

    /// <summary>The sites listed, newest first; the counts cover all of them.</summary>
    public const int MaxSites = 500;

    public static string Path => Route;

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly TimeProvider time;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        if (httpContext.User.Identity?.IsAuthenticated != true || !await permissions.IsAllowedAsync("Read", OperatorDashboardActions.TypeName, ct))
            return Results.NotFound();

        var now = time.GetUtcNow().UtcDateTime;
        var dayAgo = now.AddDays(-1);
        var weekAgo = now.AddDays(-7);

        var maintenance = store.Maintenance.ForDatabase(store.Database);
        var collections = await maintenance.SendAsync(new GetDetailedCollectionStatisticsOperation(), ct);
        var database = await maintenance.SendAsync(new GetDetailedStatisticsOperation(), ct);
        int Count(string collection) => collections.Collections.TryGetValue(collection, out var c) ? (int)c.CountOfDocuments : 0;

        using var session = store.OpenAsyncSession();
        var verifiedSites = await session.Query<Site>().CountAsync(s => s.IsVerified, ct);
        var auditsLastDay = await session.Query<AuditRun>().CountAsync(r => r.StartedAtUtc >= dayAgo, ct);
        var weekRuns = await session.Query<AuditRun>().Where(r => r.StartedAtUtc >= weekAgo).Select(r => r.Status).Take(10_000).ToListAsync(ct);
        var running = await session.Query<AuditRun>().CountAsync(r => r.Status.In(AuditRunStatus.InProgress), ct);
        var refusals = await session.Query<AuditLogEntry>()
            .Where(e => e.AtUtc >= weekAgo && e.Kind.In(AuditLogKinds.QuotaRefused, AuditLogKinds.CoolDownRefused, AuditLogKinds.GateRefused))
            .Select(e => e.Kind).Take(10_000).ToListAsync(ct);

        var sites = await session.Query<Site>().OrderByDescending(s => s.CreatedAtUtc).Take(MaxSites).ToListAsync(ct);
        var organizationIds = sites.Select(s => s.OrganizationId).OfType<string>().Distinct().ToArray();
        var organizations = organizationIds.Length > 0 ? await session.LoadAsync<Organization>(organizationIds, ct) : new Dictionary<string, Organization>();

        return Results.Json(new
        {
            generatedAtUtc = now,
            counts = new
            {
                users = Count("SparkUsers"),
                organizations = Count("Organizations"),
                sites = Count("Sites"),
                verifiedSites,
                audits = Count("AuditRuns"),
                pageResults = Count("PageResults"),
            },
            audits = new
            {
                lastDay = auditsLastDay,
                lastWeek = weekRuns.Count,
                running,
                byStatusLastWeek = weekRuns.GroupBy(s => s).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            refusalsLastWeek = refusals.GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            database = new
            {
                sizeOnDiskBytes = database.SizeOnDisk?.SizeInBytes,
                documents = collections.CountOfDocuments,
                collections = collections.Collections.Values
                    .OrderByDescending(c => c.Size?.SizeInBytes ?? 0)
                    .Select(c => new { name = c.Name, documents = c.CountOfDocuments, sizeBytes = c.Size?.SizeInBytes }),
            },
            sites = sites.Select(s => new
            {
                id = s.Id,
                origin = s.Origin,
                organization = s.OrganizationId is { } o && organizations.TryGetValue(o, out var org) && org is not null ? org.Name : null,
                personal = s.OrganizationId is { } p && organizations.TryGetValue(p, out var porg) && porg is { IsPersonal: true },
                verified = s.IsVerified,
                latestScore = s.LatestScore,
                latestGrade = s.LatestGrade,
                latestRunAtUtc = s.LatestRunAtUtc,
                createdAtUtc = s.CreatedAtUtc,
            }),
        });
    }
}
