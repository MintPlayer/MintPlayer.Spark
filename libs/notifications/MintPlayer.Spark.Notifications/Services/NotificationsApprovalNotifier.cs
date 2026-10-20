using MintPlayer.Spark.Approvals;

namespace MintPlayer.Spark.Notifications.Services;

/// <summary>
/// Delivers approval notices (<c>docs/commerce_PRD.md</c> §4.13) as notifications: in-app, mail and push by each
/// user's preference for the request's organization. Registered by <c>AddNotifications()</c>; with it, Approvals
/// sends no mail of its own. Only the contracts are referenced, so an application with Notifications but without
/// Approvals gets no approval types.
/// </summary>
internal sealed class NotificationsApprovalNotifier(ISparkNotifier notifier) : IApprovalNotifier
{
    /// <summary>The notification category (and push topic) of approval notices.</summary>
    public const string Category = "approval";

    public async Task NotifyAsync(ApprovalNotice notice, CancellationToken cancellationToken)
        => await notifier.NotifyAsync(new SparkNotification
        {
            UserIds = notice.UserIds,
            Title = notice.Title.Length > 200 ? notice.Title[..200] : notice.Title,
            Body = notice.Body is { Length: > 2000 } body ? body[..2000] : notice.Body,
            Url = notice.Url,
            OrganizationId = notice.Request.OrganizationId,
            Category = Category,
            Urgent = false,
        }, cancellationToken);
}
