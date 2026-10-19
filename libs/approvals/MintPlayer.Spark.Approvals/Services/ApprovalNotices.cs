using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Approvals.Services;

/// <summary>
/// Tells people about requests: the deciders about a new one, the requester about how it ended. Every registered
/// <see cref="IApprovalNotifier"/> delivers; with none, a MailManager mail per recipient (deduplicated per request,
/// event, status and user). Each notice also updates the open pages: the request document over the live socket, and
/// a private <c>approvals:inbox</c> frame per recipient for an inbox badge.
/// </summary>
/// <remarks>Never throws: a notice that fails is logged, the decision stands.</remarks>
internal sealed partial class ApprovalNotices
{
    /// <summary>The private live topic a recipient's open pages follow.</summary>
    public const string InboxTopic = "approvals:inbox";

    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptions<SparkApprovalsOptions> options;
    [Inject] private readonly ILogger<ApprovalNotices> logger;

    /// <summary>The request's page, app-relative.</summary>
    public static string PageOf(string requestId) => "/po/approval-request/" + Uri.EscapeDataString(requestId);

    /// <summary>A new request: its listed deciders (never the requester).</summary>
    public Task SubmittedAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var recipients = request.DeciderIds.Where(id => id != request.RequestedBy).Distinct(StringComparer.Ordinal).ToList();
        return SendAsync(ApprovalNoticeEvents.Submitted, request, recipients, "approvals.noticeSubmitted", cancellationToken);
    }

    /// <summary>The request ended: its requester, unless they ended it themselves.</summary>
    public Task DecidedAsync(ApprovalRequest request, string? by, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> recipients = request.RequestedBy is { Length: > 0 } requester && requester != by ? [requester] : [];
        var key = request.Status switch
        {
            ApprovalStatuses.Approved => "approvals.noticeApproved",
            ApprovalStatuses.Rejected => "approvals.noticeRejected",
            ApprovalStatuses.Revoked => "approvals.noticeRevoked",
            ApprovalStatuses.Expired => "approvals.noticeExpired",
            ApprovalStatuses.Stale => "approvals.noticeStale",
            _ => "approvals.noticeWithdrawn",
        };
        return SendAsync(ApprovalNoticeEvents.Decided, request, recipients, key, cancellationToken);
    }

    private async Task SendAsync(string noticeEvent, ApprovalRequest request, IReadOnlyCollection<string> recipients, string titleKey, CancellationToken cancellationToken)
    {
        Live(request, recipients);
        if (recipients.Count == 0)
            return;

        try
        {
            var manager = services.GetService<IManager>();
            var title = manager?.GetTranslatedMessage(titleKey, request.Title) ?? request.Title;
            var notice = new ApprovalNotice
            {
                Event = noticeEvent,
                Request = request.ToInfo(),
                UserIds = recipients,
                Title = title,
                Body = noticeEvent == ApprovalNoticeEvents.Submitted ? request.Summary : request.Reason,
                Url = PageOf(request.Id!),
            };

            var notifiers = services.GetServices<IApprovalNotifier>().ToList();
            if (notifiers.Count == 0)
            {
                await MailAsync(notice, request, cancellationToken);
                return;
            }
            foreach (var notifier in notifiers)
            {
                try { await notifier.NotifyAsync(notice, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Approval notifier {Notifier} failed for {RequestId}.", notifier.GetType().Name, request.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The {Event} notice of approval request {RequestId} was not delivered.", noticeEvent, request.Id);
        }
    }

    private void Live(ApprovalRequest request, IReadOnlyCollection<string> recipients)
    {
        try
        {
            // A write that only touched the request is not a persistent-object commit: say so, or open pages wait for
            // the revalidation.
            services.GetService<ISparkLiveNotifier>()?.Changed(request.Id!, "ApprovalRequests");
            if (services.GetService<ISparkLiveTopics>() is { } topics)
            {
                var frame = new ApprovalInboxFrame(request.Id!, request.Status);
                foreach (var userId in recipients.Append(request.RequestedBy).OfType<string>().Distinct(StringComparer.Ordinal))
                    topics.PublishPrivate("user:" + userId, InboxTopic, frame);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No live update for approval request {RequestId}.", request.Id);
        }
    }

    private async Task MailAsync(ApprovalNotice notice, ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer)
        {
            logger.LogDebug("No IApprovalNotifier and no MailManager: the {Event} notice of {RequestId} reaches open pages only.", notice.Event, request.Id);
            return;
        }

        var link = Absolute(notice.Url);
        using var session = store.OpenAsyncSession();
        var users = await session.LoadAsync<SparkUser>(notice.UserIds, cancellationToken);
        var template = notice.Event == ApprovalNoticeEvents.Submitted ? options.Value.Mail.Submitted : options.Value.Mail.Decided;
        foreach (var user in users.Values)
        {
            if (user is not { Email: { Length: > 0 } email, EmailConfirmed: true })
                continue;
            await mailer.SendAsync(new SparkMailRequest
            {
                Template = template,
                To = email,
                ToName = user.UserName,
                Culture = user.PreferredCulture,
                Data = new
                {
                    title = request.Title,
                    summary = request.Summary,
                    status = request.Status,
                    reason = request.Reason,
                    link,
                    user_name = user.UserName,
                },
                DeduplicationKey = $"approvals:{request.Id}:{notice.Event}:{request.Status}:{user.Id}",
            }, cancellationToken);
        }
    }

    private string? Absolute(string url)
    {
        var origin = SparkPublicBaseUrl.Resolve(
            services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value.Links.PublicBaseUrl,
            services.GetService<IConfiguration>(),
            services.GetService<IHostEnvironment>(),
            services.GetService<IHttpContextAccessor>()?.HttpContext);
        return origin is null ? url : origin.TrimEnd('/') + url;
    }
}

/// <summary>The private inbox frame: which request changed, and its status. Never its content.</summary>
public sealed record ApprovalInboxFrame(string RequestId, string Status);
