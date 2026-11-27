using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Notifications;

/// <summary>
/// #536: the notifier on the neutral driver, the database <c>SPARK_TEST_DATABASE</c> picks. Without an account
/// directory a user id is taken at its word (in-app only); the user's preference is read from the store, and the
/// stored notification gets its expiry from the store's stamper (the library model's 90 days) or, for a visitor,
/// the visitor lifetime. A mail goes out from the notification's fan-out message once the caller's save commits it,
/// through Messaging on the same store.
/// </summary>
public class NeutralNotificationTests : SparkTestDriver
{
    public sealed class NotificationsContext : SparkContext;

    private sealed record Account(string Id, string? UserName, string? Email, bool EmailConfirmed, string? PreferredCulture = null) : ISparkAccount;

    private sealed class AccountDirectory(params Account[] accounts) : ISparkAccountDirectory
    {
        public Task<IReadOnlyDictionary<string, ISparkAccount>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, ISparkAccount>>(accounts
                .Where(a => ids.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(a => a.Id, ISparkAccount (a) => a, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Records what the fan-out queues; MailManager's own delivery is not what this class tests.</summary>
    private sealed class RecordingMailer : ISparkMailer
    {
        public ConcurrentQueue<SparkMailRequest> Sent { get; } = new();

        public Task<SparkMailReceipt> SendAsync(SparkMailRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(request);
            return Task.FromResult(new SparkMailReceipt(Guid.NewGuid().ToString("N"), Suppressed: false));
        }

        public Task SendCampaignAsync(SparkMailCampaign campaign, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Notifications send no campaigns.");
    }

    private SparkEndpointFactory<NotificationsContext> NewHost(Action<IServiceCollection>? configureServices = null, bool messaging = false) => new(
        Database,
        [],
        configureServices: configureServices,
        configureSpark: spark =>
        {
            if (messaging)
                spark.AddMessaging(o => o.FallbackPollInterval = TimeSpan.FromMilliseconds(250));
            spark.AddNotifications(o => o.PublicBaseUrl = "https://spark.example/");
        });

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
        {
            receipt = await scope.ServiceProvider.GetRequiredService<ISparkNotifier>().NotifyAsync(new SparkNotification
            {
                UserIds = ["users/ann", "users/quiet"],
                VisitorIds = [visitor],
                Title = "Score dropped",
                Url = "/runs/42",
            });
            // The notifier writes in the scope's session and never saves (#536 D50).
            await scope.ServiceProvider.GetRequiredService<ISparkSession>().SaveChangesAsync();
        }

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

    [Fact]
    public async Task A_confirmed_users_mail_goes_out_from_the_fan_out_after_the_callers_save()
    {
        var mailer = new RecordingMailer();
        await using var host = NewHost(
            services =>
            {
                services.AddSingleton<ISparkAccountDirectory>(new AccountDirectory(
                    new Account("users/ann", "Ann", "ann@example.com", EmailConfirmed: true),
                    new Account("users/bob", "Bob", "bob@example.com", EmailConfirmed: false)));
                services.AddSingleton<ISparkMailer>(mailer);
            },
            messaging: true);

        SparkNotifyReceipt receipt;
        using (var scope = host.CreateScope())
        {
            receipt = await scope.ServiceProvider.GetRequiredService<ISparkNotifier>().NotifyAsync(new SparkNotification
            {
                UserIds = ["users/ann", "users/bob"],
                Title = "Score dropped",
                Url = "/runs/42",
            });
            mailer.Sent.Should().BeEmpty("the fan-out waits in the outbox, which only the caller's save publishes");
            // The notifier writes in the scope's session and never saves (#536 D50).
            await scope.ServiceProvider.GetRequiredService<ISparkSession>().SaveChangesAsync();
        }

        receipt.Mails.Should().Be(1, "only ann's address is confirmed");
        // The fan-out message is stored, fed, claimed and handled by Messaging on this store.
        await AsyncWait.UntilAsync(() => !mailer.Sent.IsEmpty, "the fan-out to queue ann's mail", TimeSpan.FromSeconds(60));
        var mail = mailer.Sent.Should().ContainSingle().Which;
        mail.To.Should().Be("ann@example.com");
        mail.DeduplicationKey.Should().StartWith("spark-notification:").And.EndWith(":users/ann", "one key per fan-out and user");
        // The in-app notifications commit with the caller's save.
        (await NotificationsAsync(host)).Select(n => n.UserId).Should().BeEquivalentTo(["users/ann", "users/bob"]);
    }
}
