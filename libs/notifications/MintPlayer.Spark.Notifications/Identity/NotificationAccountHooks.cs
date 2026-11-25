using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Notifications.Models;

namespace MintPlayer.Spark.Notifications.Identity;

/// <summary>Deleting an account deletes its notifications, devices and preferences (D26).</summary>
internal sealed class NotificationAccountDeletion(ISparkStore store) : ISparkAccountDeletionHandler
{
    public async Task OnDeletingAccountAsync(ISparkAccount user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;

        using var session = store.OpenSession();
        foreach (var device in await session.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), 0, 1024, cancellationToken))
            session.Delete(device);
        foreach (var preference in await session.LoadStartingWithAsync<NotificationPreference>(NotificationPreference.PrefixFor(userId), 0, 1024, cancellationToken))
            session.Delete(preference);
        await session.SaveChangesAsync(cancellationToken);

        // Notifications have random ids: one set-based delete, which sees every committed one (RavenDB waits for its index).
        await session.DeleteWhereAsync<Notification>(n => n.UserId == userId, cancellationToken);
    }
}

/// <summary>The personal-data export's <c>notifications</c> section: notifications, devices (without their endpoints' secrets) and preferences.</summary>
internal sealed class NotificationPersonalData(ISparkStore store) : ISparkPersonalDataContributor
{
    public string Name => "notifications";

    public async Task<object?> GetPersonalDataAsync(ISparkAccount user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;

        using var session = store.OpenSession();
        var notifications = await session.ToListAsync(session.Query<Notification>(TimeSpan.FromSeconds(30)).Where(n => n.UserId == userId).Take(10_000), cancellationToken);
        var devices = await session.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), 0, 1024, cancellationToken);
        var preferences = await session.LoadStartingWithAsync<NotificationPreference>(NotificationPreference.PrefixFor(userId), 0, 1024, cancellationToken);

        return new
        {
            notifications = notifications.Select(n => new { Id = n.Id.Value, n.OrganizationId, n.Title, n.Body, n.Url, n.Category, n.CreatedAtUtc, n.IsRead, n.ReadAtUtc }),
            devices = devices.Select(d => new { d.Service, d.Device, d.CreatedAtUtc }),
            preferences = preferences.Select(p => new { p.OrganizationId, p.InApp, p.Email, p.Push }),
        };
    }
}
