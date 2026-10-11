using Microsoft.Extensions.Options;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.WebPush;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Notifications.Services;

/// <summary>What a signed-in user does with their own notifications and devices (the <c>/spark/notifications</c> endpoints).</summary>
internal sealed class NotificationInbox(IDocumentStore store, IOptions<SparkNotificationsOptions> options, TimeProvider timeProvider)
{
    internal const int MarkAllLimit = 1024;

    /// <summary>Marks <paramref name="ids"/> (or every unread one) of <paramref name="userId"/> read. Ids of other users are ignored.</summary>
    public async Task<int> MarkReadAsync(string userId, IReadOnlyCollection<string>? ids, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        List<Notification> targets;
        if (ids is { Count: > 0 })
        {
            var loaded = await session.LoadAsync<Notification>(ids.Distinct().Take(MarkAllLimit), cancellationToken);
            targets = loaded.Values.Where(n => n is not null && n.UserId == userId).ToList()!;
        }
        else
        {
            // The values are parameters (never spliced). The index may lag a just-stored notification by a few
            // milliseconds; a click on "mark all" a moment after a push is answered by the next one.
            targets = await session.Query<Notification>()
                .Where(n => n.UserId == userId && n.IsRead != true)
                .Take(MarkAllLimit)
                .ToListAsync(cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changed = 0;
        foreach (var notification in targets.Where(n => !n.IsRead))
        {
            notification.IsRead = true;
            notification.ReadAtUtc = now;
            changed++;
        }
        if (changed > 0)
            await session.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>Stores (or refreshes) one device of <paramref name="userId"/>.</summary>
    /// <exception cref="ArgumentException">A malformed subscription, or a push service that is not allowed.</exception>
    public async Task<PushSubscription> SubscribeAsync(string userId, WebPushSubscription subscription, string? device, CancellationToken cancellationToken)
    {
        subscription.Validate();
        var endpoint = new Uri(subscription.Endpoint);
        if (!options.Value.WebPush.IsAllowedService(endpoint))
            throw new ArgumentException("This push service is not allowed (Spark:Notifications:WebPush:AllowedPushServices).");

        using var session = store.OpenAsyncSession();
        var id = PushSubscription.IdFor(userId, subscription.Endpoint);
        var stored = await session.LoadAsync<PushSubscription>(id, cancellationToken);
        if (stored is null)
        {
            stored = new PushSubscription { Id = id, UserId = userId, Endpoint = subscription.Endpoint, CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime };
            await session.StoreAsync(stored, id, cancellationToken);

            // One device too many drops the oldest, so a user who reinstalls a browser a hundred times keeps a bounded list.
            var existing = await session.Advanced.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), pageSize: 1024, token: cancellationToken);
            var others = existing.Where(e => e.Id != id).OrderBy(e => e.CreatedAtUtc).ToList();
            var max = Math.Max(1, options.Value.WebPush.MaxDevicesPerUser);
            foreach (var old in others.Take(Math.Max(0, others.Count + 1 - max)))
                session.Delete(old);
        }
        stored.P256dh = subscription.P256dh;
        stored.Auth = subscription.Auth;
        stored.Service = endpoint.Host;
        stored.Device = device is { Length: > 200 } ? device[..200] : device;
        await session.SaveChangesAsync(cancellationToken);
        return stored;
    }

    /// <summary>Forgets the device with <paramref name="endpoint"/>, if it is <paramref name="userId"/>'s.</summary>
    public async Task<bool> UnsubscribeAsync(string userId, string endpoint, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var id = PushSubscription.IdFor(userId, endpoint);
        if (await session.LoadAsync<PushSubscription>(id, cancellationToken) is not { } stored)
            return false;
        session.Delete(stored);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }
}
