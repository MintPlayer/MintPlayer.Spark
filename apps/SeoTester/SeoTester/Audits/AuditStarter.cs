using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Quotas;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using SeoTester.Catalogue;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Messaging;
using SeoTester.Settings;

namespace SeoTester.Audits;

/// <summary>What starting an audit did.</summary>
/// <param name="Joined">An audit of the same origin was running in an organization of the caller's, which they joined (D17).</param>
public sealed record AuditStartResult(string RunId, bool Joined);

/// <summary>
/// <c>StartAudit</c> and <c>StartFullCrawl</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3, §6, D11, D14, D17, D18, D37).
/// The caller's role on the site (Member) was checked by the action's row rule. In order:
/// <list type="number">
/// <item>a full-site crawl needs a verified site and the deep-crawl gate;</item>
/// <item>an audit of the same origin running in an organization where the caller is a Member or more is
/// <b>joined</b>: no quota, no cool-down, no new run;</item>
/// <item>an unverified target in its cool-down (6 h, across all users) is refused with 429;</item>
/// <item>the caller's quota (<c>audits</c>, <c>verified-audits</c> or <c>full-crawls</c>) is consumed, or 429;</item>
/// <item>the run (limits and scoring snapshot fixed now), the cool-down, the log entry and the first pipeline message
/// (<see cref="SiteProbeMessage"/>) are saved in one commit.</item>
/// </list>
/// Every start, join and refusal is written to the abuse log.
/// </summary>
public sealed partial class AuditStarter(
    IAsyncDocumentSession session,
    IMessageOutbox outbox,
    ISparkOrganizations organizations,
    ISparkQuotas quotas,
    ISparkSettings settings,
    CatalogueSnapshots snapshots,
    AuditLog auditLog,
    TimeProvider time)
{
    public async Task<AuditStartResult> StartAsync(Site site, string userId, bool fullCrawl, CancellationToken cancellationToken)
    {
        var deepGate = await settings.GetAsync(SeoSettings.DeepCrawl, cancellationToken);
        var deep = Allows(deepGate, site.IsVerified);

        if (fullCrawl && !(site.IsVerified && deep))
        {
            await auditLog.WriteAsync(AuditLogKinds.GateRefused, userId, site, runId: null, detail: "full crawl", cancellationToken);
            throw new SparkValidationException(site.IsVerified
                ? "Full-site crawls are switched off."
                : "A full-site crawl needs a verified site. Verify your ownership first.");
        }

        if (await JoinAsync(site, userId, fullCrawl, cancellationToken) is { } joined)
            return joined;

        var now = time.GetUtcNow().UtcDateTime;
        TargetCooldown? cooldown = null;
        if (!site.IsVerified)
        {
            cooldown = await session.LoadAsync<TargetCooldown>(TargetCooldown.IdFor(site.Host), cancellationToken);
            if (cooldown is { ExpiresAtUtc: { } until } && until > now)
                await RefuseAsync(AuditLogKinds.CoolDownRefused, site, userId,
                    $"{site.Host} was audited recently. An unverified site can be audited again after {until:yyyy-MM-dd HH:mm} UTC; verify your ownership to audit it any time.",
                    until - now, cancellationToken);
        }

        var quota = fullCrawl ? SeoQuotas.FullCrawls : site.IsVerified ? SeoQuotas.VerifiedAudits : SeoQuotas.Audits;
        var decision = await quotas.TryConsumeAsync(quota, userId, cancellationToken);
        if (!decision.Allowed)
            await RefuseAsync(AuditLogKinds.QuotaRefused, site, userId,
                fullCrawl ? "You used your full-site crawl for this week." : "You started as many audits as allowed today. Try again later.",
                decision.RetryAfter, cancellationToken);

        var run = await NewRunAsync(site, userId, deep, fullCrawl, now, cancellationToken);
        await session.StoreAsync(run, cancellationToken);

        if (!site.IsVerified)
        {
            var until = now + await settings.GetAsync(SeoSettings.TargetCoolDown, cancellationToken);
            // Conditional on the version read above (an empty change vector: "must not exist yet"), so of two starts at
            // the same moment one fails the commit and is refused below. An expired one not yet swept is replaced.
            var changeVector = string.Empty;
            if (cooldown is not null)
            {
                changeVector = session.Advanced.GetChangeVectorFor(cooldown);
                session.Advanced.Evict(cooldown);
            }
            await session.StoreAsync(new TargetCooldown { Host = site.Host, RunId = run.Id, ExpiresAtUtc = until }, changeVector, TargetCooldown.IdFor(site.Host), cancellationToken);
        }

        await session.StoreAsync(await auditLog.EntryAsync(AuditLogKinds.AuditStarted, userId, site, run.Id, fullCrawl ? "full crawl" : null, cancellationToken), cancellationToken);
        await outbox.EnqueueAsync(session, new SiteProbeMessage(run.Id!), cancellationToken: cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            await RefuseAsync(AuditLogKinds.CoolDownRefused, site, userId, $"{site.Host} is being audited by someone else right now.", null, cancellationToken);
        }
        return new AuditStartResult(run.Id!, Joined: false);
    }

    /// <summary>Joins an audit of the site's origin running in one of the caller's organizations (Member or more), if any.</summary>
    private async Task<AuditStartResult?> JoinAsync(Site site, string userId, bool fullCrawl, CancellationToken cancellationToken)
    {
        var memberships = await organizations.GetMembershipsAsync(userId, cancellationToken);
        var organizationIds = memberships.Where(m => m.Value >= OrganizationRole.Member).Select(m => m.Key).ToList();
        if (organizationIds.Count == 0)
            return null;

        var origin = site.Origin;
        var running = await session.Query<AuditRun>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(r => r.Origin == origin && r.Status.In(AuditRunStatus.InProgress) && r.OrganizationId.In(organizationIds))
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);
        var run = running.FirstOrDefault(r => !fullCrawl || r.IsFullCrawl);
        if (run is null)
            return null;

        if (!run.Participants.Contains(userId))
            run.Participants.Add(userId);
        await session.StoreAsync(await auditLog.EntryAsync(AuditLogKinds.AuditJoined, userId, site, run.Id, null, cancellationToken), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return new AuditStartResult(run.Id!, Joined: true);
    }

    private async Task<AuditRun> NewRunAsync(Site site, string userId, bool deep, bool fullCrawl, DateTime now, CancellationToken cancellationToken)
    {
        var probes = await settings.GetAsync(SeoSettings.IntrusiveProbes, cancellationToken);
        var snapshot = await snapshots.CreateAsync(session, cancellationToken);
        return new AuditRun
        {
            SiteId = site.Id,
            OrganizationId = site.OrganizationId,
            Origin = site.Origin,
            Status = AuditRunStatus.Queued,
            Phase = "queued",
            StartedBy = userId,
            Participants = [userId],
            StartedAtUtc = now,
            IsVerified = site.IsVerified,
            DeepCrawl = deep,
            IsFullCrawl = fullCrawl,
            IntrusiveProbes = Allows(probes, site.IsVerified),
            MaxPages = fullCrawl ? await settings.GetAsync(SeoSettings.FullCrawlMaxPages, cancellationToken)
                : await settings.GetAsync(deep ? SeoSettings.VerifiedPagesPerRun : SeoSettings.UnverifiedPagesPerRun, cancellationToken),
            WallTimeMinutes = (int)Math.Ceiling((fullCrawl ? await settings.GetAsync(SeoSettings.FullCrawlWallTime, cancellationToken)
                : await settings.GetAsync(deep ? SeoSettings.VerifiedWallTime : SeoSettings.UnverifiedWallTime, cancellationToken)).TotalMinutes),
            ConnectionsPerHost = await settings.GetAsync(deep ? SeoSettings.VerifiedConnectionsPerHost : SeoSettings.UnverifiedConnectionsPerHost, cancellationToken),
            RenderedSample = await settings.GetAsync(deep ? SeoSettings.VerifiedRenderedSample : SeoSettings.UnverifiedRenderedSample, cancellationToken),
            PsiSample = await settings.GetAsync(deep ? SeoSettings.VerifiedPsiSample : SeoSettings.UnverifiedPsiSample, cancellationToken),
            ScoringSnapshot = snapshot,
            ScoringVersion = snapshot.Version,
        };
    }

    private async Task RefuseAsync(string kind, Site site, string userId, string message, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        await auditLog.WriteAsync(kind, userId, site, runId: null, detail: message, cancellationToken);
        throw new SparkThrottledException(message, retryAfter);
    }

    /// <summary>Whether a gate admits a site (D11, Q-M7a).</summary>
    public static bool Allows(SeoFeatureScope gate, bool verified) => gate switch
    {
        SeoFeatureScope.AllSites => true,
        SeoFeatureScope.VerifiedOnly => verified,
        _ => false,
    };
}
