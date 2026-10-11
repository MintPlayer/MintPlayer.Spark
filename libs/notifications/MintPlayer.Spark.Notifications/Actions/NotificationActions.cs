using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Notifications.Endpoints;
using MintPlayer.Spark.Notifications.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Notifications.Actions;

/// <summary>
/// A user's own notifications, and nobody else's: the row rule is <c>UserId == caller</c> on every action, so
/// another user's notification is the missing-row 404 (#453) and an anonymous caller has none. The rule rides
/// every read path, the live socket included (X4).
/// </summary>
public partial class NotificationActions : DefaultPersistentObjectActions<Notification>
{
    [Inject] private readonly IAsyncDocumentSession session;
    // Not named httpContextAccessor: the base declares an optional one of that name, and MintPlayer.SourceGenerators
    // 12.2.0 then emits the parameter twice (CS0100).
    [Inject] private readonly IHttpContextAccessor contextAccessor;

    public override Task<Expression<Func<Notification, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<Notification, bool>>?>(OwnRows.For<Notification>(contextAccessor, n => n.UserId));

    /// <summary>The list (<c>"live": true</c>): newest first by the query's sort.</summary>
    public IRavenQueryable<Notification> MyNotifications() => session.Query<Notification>();

    /// <summary>The unread ones: the bell's count is this query's <c>totalItems</c>, followed live.</summary>
    public IQueryable<Notification> UnreadNotifications() => session.Query<Notification>().Where(n => n.IsRead != true);
}

/// <summary>A user's devices. Listing and deleting (unsubscribing a lost device) only; the browser subscribes through <c>/spark/notifications/push/subscribe</c>.</summary>
public partial class PushSubscriptionActions : DefaultPersistentObjectActions<PushSubscription>
{
    [Inject] private readonly IAsyncDocumentSession session;
    // Not named httpContextAccessor: the base declares an optional one of that name, and MintPlayer.SourceGenerators
    // 12.2.0 then emits the parameter twice (CS0100).
    [Inject] private readonly IHttpContextAccessor contextAccessor;

    public override Task<Expression<Func<PushSubscription, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<PushSubscription, bool>>?>(OwnRows.For<PushSubscription>(contextAccessor, s => s.UserId));

    public IRavenQueryable<PushSubscription> MyPushSubscriptions() => session.Query<PushSubscription>();
}

/// <summary>A user's channel choices: one row per organization plus an optional default (no organization).</summary>
public partial class NotificationPreferenceActions : DefaultPersistentObjectActions<NotificationPreference>
{
    [Inject] private readonly IAsyncDocumentSession session;
    // Not named httpContextAccessor: the base declares an optional one of that name, and MintPlayer.SourceGenerators
    // 12.2.0 then emits the parameter twice (CS0100).
    [Inject] private readonly IHttpContextAccessor contextAccessor;

    public override Task<Expression<Func<NotificationPreference, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<NotificationPreference, bool>>?>(OwnRows.For<NotificationPreference>(contextAccessor, p => p.UserId));

    public IRavenQueryable<NotificationPreference> MyNotificationPreferences() => session.Query<NotificationPreference>();
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
