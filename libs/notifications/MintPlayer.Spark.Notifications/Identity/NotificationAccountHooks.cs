using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Notifications.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Notifications.Identity;

/// <summary>Deleting an account deletes its notifications, devices and preferences (D26).</summary>
internal sealed class NotificationAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;

        using var session = store.OpenAsyncSession();
        foreach (var device in await session.Advanced.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), pageSize: 1024, token: cancellationToken))
            session.Delete(device);
        foreach (var preference in await session.Advanced.LoadStartingWithAsync<NotificationPreference>("NotificationPreferences/" + userId + "/", pageSize: 1024, token: cancellationToken))
            session.Delete(preference);
        await session.SaveChangesAsync(cancellationToken);

        // Notifications have random ids: delete them by query, in pages, waiting for the index so none is missed.
        while (true)
        {
            using var page = store.OpenAsyncSession();
            var batch = await page.Query<Notification>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(n => n.UserId == userId)
                .Take(512)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                break;
            foreach (var notification in batch)
                page.Delete(notification);
            await page.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>The personal-data export's <c>notifications</c> section: notifications, devices (without their endpoints' secrets) and preferences.</summary>
internal sealed class NotificationPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    public string Name => "notifications";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;

        using var session = store.OpenAsyncSession();
        var notifications = await session.Query<Notification>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(n => n.UserId == userId)
            .Take(10_000)
            .ToListAsync(cancellationToken);
        var devices = await session.Advanced.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), pageSize: 1024, token: cancellationToken);
        var preferences = await session.Advanced.LoadStartingWithAsync<NotificationPreference>("NotificationPreferences/" + userId + "/", pageSize: 1024, token: cancellationToken);

        return new
        {
            notifications = notifications.Select(n => new { n.Id, n.OrganizationId, n.Title, n.Body, n.Url, n.Category, n.CreatedAtUtc, n.IsRead, n.ReadAtUtc }),
            devices = devices.Select(d => new { d.Service, d.Device, d.CreatedAtUtc }),
            preferences = preferences.Select(p => new { p.OrganizationId, p.InApp, p.Email, p.Push }),
        };
    }
}
