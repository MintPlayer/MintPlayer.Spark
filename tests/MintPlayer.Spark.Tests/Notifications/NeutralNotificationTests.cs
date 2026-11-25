using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Notifications;

/// <summary>
/// #536: the notifier on the neutral driver, the database <c>SPARK_TEST_DATABASE</c> picks. Without an account
/// directory a user id is taken at its word (in-app only); the user's preference is read from the store, and the
/// stored notification gets its expiry from the store's stamper (the library model's 90 days) or, for a visitor,
/// the visitor lifetime.
/// </summary>
public class NeutralNotificationTests : SparkTestDriver
{
    public sealed class NotificationsContext : SparkContext;

    private SparkEndpointFactory<NotificationsContext> NewHost() => new(
        Database,
        [],
        configureSpark: spark => spark.AddNotifications());

    private static async Task<List<Notification>> NotificationsAsync(SparkEndpointFactory<NotificationsContext> host)
    {
        using var session = host.GetService<ISparkStore>().OpenSession();
        return await session.ToListAsync(session.Query<Notification>());
    }

    [Fact]
    public async Task A_user_and_a_visitor_are_notified_in_app_and_a_user_who_turned_in_app_off_is_not()
    {
        await using var host = NewHost();

        using (var session = host.GetService<ISparkStore>().OpenSession())
        {
            var preference = new NotificationPreference { Id = NotificationPreference.IdFor("users/quiet", null), UserId = "users/quiet", InApp = false };
            await session.StoreAsync(preference, preference.Id.Value);
            await session.SaveChangesAsync();
        }

        var visitor = SparkVisitorOptions.IdPrefix + "neutral";
        SparkNotifyReceipt receipt;
        using (var scope = host.CreateScope())
            receipt = await scope.ServiceProvider.GetRequiredService<ISparkNotifier>().NotifyAsync(new SparkNotification
            {
                UserIds = ["users/ann", "users/quiet"],
                VisitorIds = [visitor],
                Title = "Score dropped",
                Url = "/runs/42",
            });

        receipt.NotificationIds.Count.Should().Be(2, "ann and the visitor; the quiet user turned in-app off");
        var stored = await NotificationsAsync(host);
        stored.Select(n => n.UserId ?? n.VisitorId).Should().BeEquivalentTo(["users/ann", visitor]);

        var visitors = stored.Single(n => n.VisitorId == visitor);
        visitors.ExpiresAtUtc!.Value.Should().BeBefore(DateTime.UtcNow.AddDays(2), "it dies with the visitor");

        // The model's default is the store's stamper (ISparkStoreExpiration); a store without the capability (InMemory)
        // stores none, and the expiration row policy has nothing to hide.
        if (!host.GetService<ISparkStore>().Capabilities.HasFlag(SparkStoreCapabilities.Expiration))
            return;
        var ann = stored.Single(n => n.UserId == "users/ann");
        ann.ExpiresAtUtc.HasValue.Should().BeTrue("the library model's expiresAfter (90 days) stamps it, X1");
        ann.ExpiresAtUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddDays(80));
    }
}
