using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Notifications.Endpoints;
using MintPlayer.Spark.Notifications.Models;

namespace MintPlayer.Spark.Notifications.Actions;

/// <summary>
/// The caller's own notifications, and nobody else's: the row rule is "<c>UserId == my user</c> or
/// <c>VisitorId == my visitor</c>" on every action (<c>docs/notification_bell_PRD.md</c> R4), so anyone else's
/// notification is the missing-row 404 (#453) and a caller with neither identity has none. The rule rides every
/// read path, the live socket included (X4).
/// </summary>
public partial class NotificationActions : DefaultPersistentObjectActions<Notification>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly ISparkCurrentPrincipal principal;

    public override Task<Expression<Func<Notification, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<Notification, bool>>?>(NotificationOwner.Of(principal).Filter());

    /// <summary>The list (<c>"live": true</c>): newest first by the query's sort.</summary>
    public IQueryable<Notification> MyNotifications() => session.Query<Notification>();

    /// <summary>The unread ones: the bell's count is this query's <c>totalItems</c>, followed live.</summary>
    public IQueryable<Notification> UnreadNotifications() => session.Query<Notification>().Where(n => n.IsRead != true);
}

/// <summary>A user's devices. Listing and deleting (unsubscribing a lost device) only; the browser subscribes through <c>/spark/notifications/push/subscribe</c>.</summary>
public partial class PushSubscriptionActions : DefaultPersistentObjectActions<PushSubscription>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<Expression<Func<PushSubscription, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<PushSubscription, bool>>?>(OwnRows.For<PushSubscription>(httpContextAccessor, s => s.UserId));

    public IQueryable<PushSubscription> MyPushSubscriptions() => session.Query<PushSubscription>();
}

/// <summary>A user's channel choices: one row per organization plus an optional default (no organization).</summary>
public partial class NotificationPreferenceActions : DefaultPersistentObjectActions<NotificationPreference>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<Expression<Func<NotificationPreference, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<NotificationPreference, bool>>?>(OwnRows.For<NotificationPreference>(httpContextAccessor, p => p.UserId));

    public IQueryable<NotificationPreference> MyNotificationPreferences() => session.Query<NotificationPreference>();
}

/// <summary>
/// Who a notification can belong to: the caller's user id, their visitor id, or both (a visitor who signed in while
/// a job was still running for the visitor). Each side counts only when present: a null side never matches the
/// documents whose field is null.
/// </summary>
internal readonly record struct NotificationOwner(string? UserId, string? VisitorId)
{
    public static NotificationOwner Of(ISparkCurrentPrincipal principal)
        => new(principal.Id is { Length: > 0 } id ? id : null, principal.VisitorId is { Length: > 0 } visitor ? visitor : null);

    public bool IsKnown => UserId is not null || VisitorId is not null;

    public bool Owns(Notification notification)
        => (UserId is not null && notification.UserId == UserId) || (VisitorId is not null && notification.VisitorId == VisitorId);

    /// <summary>The row filter. The captured values become query parameters, never spliced RQL.</summary>
    public Expression<Func<Notification, bool>> Filter()
    {
        var userId = UserId;
        var visitorId = VisitorId;
        if (userId is not null && visitorId is not null)
            return n => n.UserId == userId || n.VisitorId == visitorId;
        if (userId is not null)
            return n => n.UserId == userId;
        if (visitorId is not null)
            return n => n.VisitorId == visitorId;
        return _ => false;
    }
}

/// <summary>The "rows of the signed-in user" predicate, with a closed door for an anonymous caller.</summary>
internal static class OwnRows
{
    public static Expression<Func<T, bool>> For<T>(IHttpContextAccessor accessor, Expression<Func<T, string?>> owner)
    {
        if (NotificationCaller.Id(accessor) is not { } userId)
            return _ => false;
        // owner(x) == userId, built as an expression so the row filter is translated to RQL with userId as a parameter.
        var body = Expression.Equal(owner.Body, Expression.Constant(userId, typeof(string)));
        return Expression.Lambda<Func<T, bool>>(body, owner.Parameters);
    }
}
