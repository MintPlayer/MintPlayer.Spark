using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Tells a seller about a dispute (GB1): its Owners, Admins and holders of <see cref="DisputeOptions.NotifyRole"/>.
/// Every <see cref="IPaymentNotifier"/> delivers; with none, a MailManager mail per user (deduplicated per dispute and
/// status). For the platform's own sales, the <see cref="SparkCommercePaymentsOptions.PlatformOrganizationId"/>'s people.
/// </summary>
internal sealed class PaymentNotices(
    IServiceProvider services,
    IDocumentStore store,
    IOptionsMonitor<SparkCommercePaymentsOptions> options,
    PaymentUrls urls,
    PaymentProviderRegistry registry,
    ILogger<PaymentNotices> logger)
{
    public async Task NotifyDisputeAsync(PaymentDispute dispute, CancellationToken cancellationToken)
    {
        var organizationId = dispute.SellerOrganizationId ?? options.CurrentValue.PlatformOrganizationId;
        if (organizationId is null)
        {
            logger.LogWarning("Dispute {DisputeId} on a platform sale, but Spark:Commerce:Payments:PlatformOrganizationId is not set: nobody is notified.", dispute.Id);
            return;
        }

        var userIds = await RecipientsAsync(organizationId, cancellationToken);
        var url = string.Format(CultureInfo.InvariantCulture, options.CurrentValue.Disputes.UrlFormat, Uri.EscapeDataString(dispute.Id!));
        var notice = new PaymentDisputeNotice(dispute, userIds, url);

        var notifiers = services.GetServices<IPaymentNotifier>().ToList();
        if (notifiers.Count == 0)
        {
            await MailAsync(notice, cancellationToken);
            return;
        }
        foreach (var notifier in notifiers)
        {
            try { await notifier.NotifyDisputeAsync(notice, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Payment notifier {Notifier} failed for dispute {DisputeId}.", notifier.GetType().Name, dispute.Id);
            }
        }
    }

    private async Task<IReadOnlyList<string>> RecipientsAsync(string organizationId, CancellationToken cancellationToken)
    {
        var role = options.CurrentValue.Disputes.NotifyRole;
        using var session = store.OpenAsyncSession();
        var members = await session.Query<OrganizationMembership>()
            .Where(m => m.OrganizationId == organizationId
                && (m.Role == OrganizationRole.Admin || m.Role == OrganizationRole.Owner || m.CustomRoles.Any(r => r == role)))
            .Take(200)
            .ToListAsync(cancellationToken);
        return members.Select(m => m.UserId).OfType<string>().Distinct().ToList();
    }

    private async Task MailAsync(PaymentDisputeNotice notice, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer)
        {
            logger.LogDebug("No IPaymentNotifier and no MailManager: dispute {DisputeId} is not announced.", notice.Dispute.Id);
            return;
        }

        string? link;
        try { link = urls.Absolute(notice.Url); }
        catch (InvalidOperationException) { link = null; }

        using var session = store.OpenAsyncSession();
        var users = await session.LoadAsync<SparkUser>(notice.UserIds, cancellationToken);
        var dispute = notice.Dispute;
        var locales = services.GetService<ISparkRecipientLocale>();
        var formatter = services.GetService<ISparkDateTimeFormatter>();
        foreach (var user in users.Values)
        {
            if (user is not { Email: { Length: > 0 } email, EmailConfirmed: true })
                continue;
            // K10: the deadline is formatted by the core facility in the recipient's own zone and culture.
            string? dueBy = null;
            if (dispute.EvidenceDueByUtc is { } due && locales is not null && formatter is not null)
                dueBy = await formatter.FormatAsync(new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Utc)),
                    await locales.ForUserAsync(user.Id, cancellationToken), cancellationToken: cancellationToken);
            await mailer.SendAsync(new SparkMailRequest
            {
                Template = options.CurrentValue.Disputes.MailTemplate,
                To = email,
                ToName = user.UserName,
                Culture = user.PreferredCulture,
                Data = new
                {
                    reference = dispute.Reference,
                    amount = dispute.Amount.Amount.ToString("0.00##", CultureInfo.InvariantCulture),
                    currency = dispute.Amount.Currency,
                    reason = dispute.ReasonText ?? dispute.Reason,
                    status = dispute.Status.ToString(),
                    due_by = dueBy,
                    manual_only = !registry.Get(dispute.Provider).Capabilities.DisputeEvidenceByApi,
                    link,
                    user_name = user.UserName,
                },
                DeduplicationKey = $"payments:dispute:{dispute.Id}:{dispute.Status}:{user.Id}",
            }, cancellationToken);
        }
    }
}
