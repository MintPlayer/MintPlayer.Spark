using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Jobs;

/// <summary>
/// Tells a seller something it must act on (a DAC7 reminder, the payout block, a statement of reasons). The default
/// implementation logs; a host wires its mail or notifications here (<c>[SparkDiscoverable]</c>, one per app).
/// </summary>
[MintPlayer.Spark.Abstractions.SparkDiscoverable]
public interface ICommerceSellerNotifier
{
    /// <summary>Notifies the admins of <paramref name="organizationId"/>.</summary>
    /// <param name="organizationId">The seller.</param>
    /// <param name="topic">What about (<c>dac7-reminder</c>, <c>payouts-blocked</c>, <c>statement-of-reasons</c>).</param>
    /// <param name="details">Translation keys or free text to include.</param>
    Task NotifyAsync(string organizationId, string topic, IReadOnlyList<string> details, CancellationToken cancellationToken);
}

internal sealed class LoggingSellerNotifier(ILogger<LoggingSellerNotifier> logger) : ICommerceSellerNotifier
{
    public Task NotifyAsync(string organizationId, string topic, IReadOnlyList<string> details, CancellationToken cancellationToken)
    {
        logger.LogInformation("Commerce seller notice for {OrganizationId}: {Topic} ({Details})", organizationId, topic, string.Join(", ", details));
        return Task.CompletedTask;
    }
}

/// <summary>
/// DAC7 (GA6): reminds sellers with missing data every <c>ReminderInterval</c>, and blocks payouts once
/// <c>RemindersBeforeBlock</c> reminders went unanswered and <c>MinimumWaitBeforeBlock</c> passed since the first
/// request (Directive (EU) 2021/514 Annex V; confirm, D37). Connected mode only. Idempotent per run.
/// </summary>
internal sealed partial class Dac7ReminderJob : ISparkCronJob
{
    public static string CronSchedule => "17 6 * * *";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptions<SparkCommerceOptions> options;
    [Inject] private readonly ICommerceSellerNotifier notifier;
    [Inject] private readonly ICommerceAudit audit;
    [Inject] private readonly TimeProvider clock;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.Merchant != MerchantMode.ConnectedSellers || !o.Sellers.Dac7.Enabled) return;
        var now = clock.GetUtcNow();
        using var session = store.OpenAsyncSession();
        var profiles = await session.Query<SellerProfile>()
            .Where(p => p.Status == SellerRegistrationStatus.Approved && p.PayoutsBlocked == false)
            .Take(1024).ToListAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            var step = Next(profile.Dac7, o.Sellers.Dac7, now);
            if (step == Dac7Step.None) continue;
            if (step == Dac7Step.Remind)
            {
                profile.Dac7.RequestedAt ??= now;
                profile.Dac7.RemindersSent++;
                profile.Dac7.LastReminderAt = now;
                await notifier.NotifyAsync(profile.OrganizationId, "dac7-reminder", profile.Dac7.Missing(), cancellationToken);
            }
            else
            {
                profile.PayoutsBlocked = true;
                await audit.RecordAsync(session, new CommerceAuditEntry
                {
                    Action = CommerceAuditActions.PayoutBlock, SubjectId = profile.Id!, OrganizationId = profile.OrganizationId,
                    Reason = "dac7-missing: payouts blocked", At = now,
                }, cancellationToken);
                await notifier.NotifyAsync(profile.OrganizationId, "payouts-blocked", profile.Dac7.Missing(), cancellationToken);
            }
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    internal enum Dac7Step { None, Remind, Block }

    /// <summary>Pure: what to do for one seller now.</summary>
    internal static Dac7Step Next(Dac7Data data, CommerceDac7Options options, DateTimeOffset now)
    {
        if (data.Missing().Count == 0) return Dac7Step.None;
        var requested = data.RequestedAt ?? now;
        if (data.RemindersSent >= options.RemindersBeforeBlock)
            return now - requested >= options.MinimumWaitBeforeBlock && (data.LastReminderAt is null || now - data.LastReminderAt >= options.ReminderInterval)
                ? Dac7Step.Block : Dac7Step.None;
        if (data.RequestedAt is null) return Dac7Step.Remind; // the first request
        return data.LastReminderAt is null || now - data.LastReminderAt >= options.ReminderInterval ? Dac7Step.Remind : Dac7Step.None;
    }
}

/// <summary>
/// Retention (§4.15) for Commerce's own document types: trader data erased once its period after the end of the
/// relationship passed (DSA Art. 30(3)); decided DSA notices and statements past their period deleted. The price
/// history and the money audit are kept (their floors are minimums and they back the accounts). Other packages sweep
/// their own types with <see cref="ICommerceRetention.GetPeriod"/>; invoices are never altered, only deleted whole.
/// </summary>
internal sealed partial class CommerceRetentionJob : ISparkCronJob
{
    public static string CronSchedule => "43 3 * * *";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ICommerceRetention retention;
    [Inject] private readonly TimeProvider clock;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        using var session = store.OpenAsyncSession();

        var traderCutoff = now - retention.GetPeriod(CommerceDocumentTypes.TraderData);
        var ended = await session.Query<SellerProfile>()
            .Where(p => p.RelationshipEndedAt != null && p.RelationshipEndedAt < traderCutoff && p.TraderDataErasedAt == null)
            .Take(256).ToListAsync(cancellationToken);
        foreach (var profile in ended)
        {
            profile.Trader = new TraderData();
            profile.Verification = null;
            profile.TraderDataErasedAt = now;
            session.Advanced.Attachments.Delete(profile, SellerProfile.IdentityDocumentAttachment);
        }

        var noticeCutoff = now - retention.GetPeriod(CommerceDocumentTypes.DsaNotices);
        foreach (var notice in await session.Query<DsaNotice>().Where(n => n.ReceivedAt < noticeCutoff && n.Status != DsaNoticeStatus.Open).Take(512).ToListAsync(cancellationToken))
            session.Delete(notice);
        foreach (var statement in await session.Query<DsaStatementOfReasons>().Where(s => s.IssuedAt < noticeCutoff).Take(512).ToListAsync(cancellationToken))
            session.Delete(statement);

        await session.SaveChangesAsync(cancellationToken);
    }
}
