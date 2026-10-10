using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Live;
using MintPlayer.Spark.Tests.Organizations;
using MintPlayer.Spark.Tests.WebPush;
using MintPlayer.Spark.WebPush;

namespace MintPlayer.Spark.Tests.Notifications;

/// <summary>
/// X10 (<c>docs/seo_analyzer_PRD.md</c>): one <see cref="ISparkNotifier"/> call reaches a user in-app (live over
/// <c>/spark/live</c>), by mail and by Web Push, through the channels of their preference for the organization.
/// The host is the organizations host (signed-in users by header, a recording mailer) with the package, Messaging
/// and a fake push service behind the package's SSRF-safe client.
/// </summary>
public class NotificationTests : OrganizationsTestHost
{
    // The library's stamped ids (UUIDv5 over 'notifications:…', App_Data/Model/*.json).
    internal const string NotificationTypeId = "411e3be6-5da0-5d84-bc21-001888fafc6d";
    internal const string MyNotificationsQueryId = "7123c201-9aed-50f9-9d9a-ede2491af7f9";
    internal const string UnreadQueryId = "a205d3e5-ff6e-5260-ac9e-e5c6b16644c9";

    private static readonly VapidDetails Vapid = VapidDetails.Generate("mailto:ops@example.com");
    private static readonly Uri Server = new("http://localhost/");

    private readonly FakePushService pushService = new();

    public NotificationTests()
    {
        ExtraLibrarySlots["notifications"] = "notifications:users";
        ExtraSpark = spark =>
        {
            spark.AddMessaging(o => o.FallbackPollInterval = TimeSpan.FromMilliseconds(250));
            spark.AddNotifications(o =>
            {
                o.WebPush.PublicKey = Vapid.PublicKey;
                o.WebPush.PrivateKey = Vapid.PrivateKey;
                o.WebPush.Subject = Vapid.Subject;
            });
            // The package's SSRF-safe client, with the network swapped for the fake push service.
            spark.Services.AddHttpClient(SparkNotificationsExtensions.WebPushHttpClient).ConfigurePrimaryHttpMessageHandler(() => pushService);
        };
    }

    private ISparkNotifier Notifier() => Factory.CreateScope().ServiceProvider.GetRequiredService<ISparkNotifier>();

    [Fact]
    public async Task A_notification_is_stored_per_user_and_only_its_user_reads_it()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");

        var receipt = await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!, bob.Id!, "SparkUsers/nobody"], Title = "Score dropped", Body = "From 91 to 74.", Url = "/runs/42" });

        receipt.NotificationIds.Count.Should().Be(2, "one per known user; an unknown id is skipped");
        var annsId = (await NotificationsOfAsync(ann.Id!)).Single();
        var bobsId = (await NotificationsOfAsync(bob.Id!)).Single();
        annsId.Should().NotBe(bobsId);

        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTypeId, id: annsId), ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTypeId, id: annsId), bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTypeId, id: annsId), null)).Status.Should().Be(HttpStatusCode.Unauthorized); // anonymous: the framework's 401
        var stored = await LoadAsync<Notification>(annsId);
        stored!.UserId.Should().Be(ann.Id);
        stored.ExpiresAtUtc.HasValue.Should().BeTrue("the library model's expiresAfter (90 days) stamps it, X1");
    }

    [Fact]
    public async Task Preferences_per_organization_choose_the_channels_and_fall_back_to_the_default_row_then_the_options()
    {
        var ann = await SeedUserAsync("ann");
        await SubscribeAsync(ann.Id!, "https://fcm.googleapis.com/fcm/send/ann-1");
        await StoreAsync(new NotificationPreference { Id = NotificationPreference.IdFor(ann.Id!, "Organizations/acme"), UserId = ann.Id, OrganizationId = "Organizations/acme", InApp = false, Email = true, Push = false });
        await StoreAsync(new NotificationPreference { Id = NotificationPreference.IdFor(ann.Id!, null), UserId = ann.Id, InApp = true, Email = false, Push = false });

        var acme = await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!], Title = "acme", OrganizationId = "Organizations/acme" });
        var other = await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!], Title = "other", OrganizationId = "Organizations/other" });

        (acme.NotificationIds.Count, acme.Mails, acme.Pushes).Should().Be((0, 1, 0));
        (other.NotificationIds.Count, other.Mails, other.Pushes).Should().Be((1, 0, 0), "no row for 'other': the default row applies");

        var bob = await SeedUserAsync("bob");
        await SubscribeAsync(bob.Id!, "https://updates.push.services.mozilla.com/wpush/v2/bob");
        var none = await Notifier().NotifyAsync(new SparkNotification { UserIds = [bob.Id!], Title = "defaults", OrganizationId = "Organizations/acme" });
        (none.NotificationIds.Count, none.Mails, none.Pushes).Should().Be((1, 1, 1), "no rows at all: Spark:Notifications:Defaults, all on");
        Mailer.Sent.Where(m => m.To == bob.Email).Should().ContainSingle().Which.Template.Should().Be(SparkNotificationMailTemplates.Notification);
    }

    [Fact]
    public async Task A_mail_goes_only_to_a_confirmed_address_with_an_absolute_link()
    {
        var ann = await SeedUserAsync("ann");
        var unconfirmed = await SeedUserAsync("uma", emailConfirmed: false);

        var receipt = await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!, unconfirmed.Id!], Title = "Hello", Url = "/runs/7" });

        receipt.Mails.Should().Be(1);
        var mail = Mailer.Sent.Should().ContainSingle().Which;
        mail.To.Should().Be(ann.Email);
        JsonSerializer.SerializeToNode(mail.Data)!["link"]!.GetValue<string>().Should().Be(PublicBaseUrl + "/runs/7");
    }

    [Fact]
    public async Task A_push_reaches_every_device_encrypted_and_a_gone_device_is_deleted()
    {
        var ann = await SeedUserAsync("ann");
        var phone = await SubscribeAsync(ann.Id!, "https://fcm.googleapis.com/fcm/send/phone");
        var laptop = await SubscribeAsync(ann.Id!, "https://web.push.apple.com/laptop");
        pushService.StatusFor["https://web.push.apple.com/laptop"] = HttpStatusCode.Gone;

        var receipt = await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!], Title = "Certificate expires", Body = "in 9 days", Url = "/sites/1", Category = "cert-expiry", Urgent = true });
        receipt.Pushes.Should().Be(2);

        await AsyncWait.UntilAsync(() => pushService.Received.Count >= 2, "both pushes to reach the push service", TimeSpan.FromSeconds(30));
        // ForAsync treats a null result as "not yet", so the deletion is polled as an existence flag.
        await AsyncWait.ForAsync(async () => (bool?)(await LoadAsync<PushSubscription>(PushSubscription.IdFor(ann.Id!, "https://web.push.apple.com/laptop")) is not null),
            exists => exists == false, "the 410 device to be deleted", exists => exists == true ? "still stored" : "deleted", TimeSpan.FromSeconds(30));
        (await LoadAsync<PushSubscription>(PushSubscription.IdFor(ann.Id!, "https://fcm.googleapis.com/fcm/send/phone"))).Should().NotBeNull();

        var toPhone = pushService.Received.Single(r => r.Uri == "https://fcm.googleapis.com/fcm/send/phone");
        toPhone.Headers["Urgency"].Should().Be("high");
        toPhone.Headers["Topic"].Should().Be("cert-expiry");
        toPhone.Headers["Authorization"].Should().StartWith("vapid t=");
        var json = JsonDocument.Parse(WebPushEncryptionTests.Receiver.Derive(toPhone.Body, phone.Key, phone.Auth).Plaintext).RootElement;
        var shown = json.GetProperty("notification");
        shown.GetProperty("title").GetString().Should().Be("Certificate expires");
        shown.GetProperty("body").GetString().Should().Be("in 9 days");
        shown.GetProperty("data").GetProperty("url").GetString().Should().Be(PublicBaseUrl + "/sites/1");
        shown.GetProperty("data").GetProperty("onActionClick").GetProperty("default").GetProperty("operation").GetString().Should().Be("navigateLastFocusedOrOpen");
        shown.GetProperty("data").GetProperty("notificationId").GetString().Should().Be(receipt.NotificationIds.Single());
        laptop.Key.Dispose();
        phone.Key.Dispose();
    }

    [Fact]
    public async Task The_unread_count_reaches_an_open_socket_without_any_notifier_code()
    {
        var ann = await SeedUserAsync("ann");
        await using var socket = await LiveTestClient.ConnectAsync(Factory.CreateWebSocketClient(), Server, r => r.Headers[OgPrincipalStartupFilter.Header] = ann.Id!);
        await socket.SubscribeQueryAsync("unread", new { queryId = UnreadQueryId, skip = 0, take = 5 });
        var first = await socket.NextAsync("result", "unread");
        first.GetProperty("result").GetProperty("totalItems").GetInt32().Should().Be(0);

        await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!], Title = "Run finished" });

        var count = await TotalAfterAsync(socket, "unread", TimeSpan.FromSeconds(15));
        count.Should().Be(1);

        // Marking it read takes it off the unread query: the bell drops to 0 over the same socket.
        (await SendAsync("/spark/notifications/read", new { ids = (string[]?)null }, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        (await TotalAfterAsync(socket, "unread", TimeSpan.FromSeconds(15))).Should().Be(0);
    }

    [Fact]
    public async Task Mark_read_touches_only_the_callers_own_notifications()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!, bob.Id!], Title = "x" });
        var annsId = (await NotificationsOfAsync(ann.Id!)).Single();
        var bobsId = (await NotificationsOfAsync(bob.Id!)).Single();

        var (status, body) = await SendAsync("/spark/notifications/read", new { ids = new[] { annsId, bobsId } }, ann.Id);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("marked").GetInt32().Should().Be(1);
        (await LoadAsync<Notification>(annsId))!.IsRead.Should().BeTrue();
        (await LoadAsync<Notification>(bobsId))!.IsRead.Should().BeFalse();
        (await SendAsync("/spark/notifications/read", new { ids = new[] { bobsId } }, null)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Subscribing_refuses_other_push_services_and_bad_keys_and_keeps_a_bounded_device_list()
    {
        var ann = await SeedUserAsync("ann");
        using (var client = Factory.CreateClient())
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/spark/notifications/push/key");
            request.Headers.Add(OgPrincipalStartupFilter.Header, ann.Id);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("publicKey").GetString().Should().Be(Vapid.PublicKey);
        }

        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p256dh = Base64Url.EncodeToString(WebPushEncryptionTests.Uncompressed(key));
        var auth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        (await SendAsync("/spark/notifications/push/subscribe", Body("https://attacker.example/collect", p256dh, auth), ann.Id)).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync("/spark/notifications/push/subscribe", Body("http://fcm.googleapis.com/fcm/send/x", p256dh, auth), ann.Id)).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync("/spark/notifications/push/subscribe", Body("https://fcm.googleapis.com/fcm/send/x", "AAAA", auth), ann.Id)).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync("/spark/notifications/push/subscribe", Body("https://fcm.googleapis.com/fcm/send/x", p256dh, auth), null)).Status.Should().Be(HttpStatusCode.Unauthorized);

        for (var i = 0; i < 22; i++)
            (await SendAsync("/spark/notifications/push/subscribe", Body($"https://fcm.googleapis.com/fcm/send/d{i}", p256dh, auth), ann.Id)).Status.Should().Be(HttpStatusCode.NoContent);
        var devices = await DevicesOfAsync(ann.Id!);
        devices.Count.Should().Be(20, "MaxDevicesPerUser: the oldest go");
        devices.Should().NotContain(PushSubscription.IdFor(ann.Id!, "https://fcm.googleapis.com/fcm/send/d0"));

        (await SendAsync("/spark/notifications/push/unsubscribe", new { endpoint = "https://fcm.googleapis.com/fcm/send/d21" }, ann.Id)).Status.Should().Be(HttpStatusCode.NoContent);
        (await DevicesOfAsync(ann.Id!)).Count.Should().Be(19);
    }

    [Fact]
    public async Task Deleting_the_account_deletes_notifications_devices_and_preferences_and_the_export_lists_them()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await SubscribeAsync(ann.Id!, "https://fcm.googleapis.com/fcm/send/ann");
        await StoreAsync(new NotificationPreference { Id = NotificationPreference.IdFor(ann.Id!, null), UserId = ann.Id, Push = false });
        await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!, bob.Id!], Title = "x" });

        using (var scope = Factory.CreateScope())
        {
            var export = scope.ServiceProvider.GetServices<ISparkPersonalDataContributor<SparkUser>>().Single(c => c.Name == "notifications");
            var data = JsonSerializer.SerializeToNode(await export.GetPersonalDataAsync(ann, CancellationToken.None))!;
            data["notifications"]!.AsArray().Count.Should().Be(1);
            data["devices"]!.AsArray().Single()!["Service"]!.GetValue<string>().Should().Be("fcm.googleapis.com");
            data["devices"]!.AsArray().Single()!.AsObject().ContainsKey("Endpoint").Should().BeFalse();
            data["preferences"]!.AsArray().Count.Should().Be(1);

            foreach (var handler in scope.ServiceProvider.GetServices<ISparkAccountDeletionHandler<SparkUser>>().Where(h => h.GetType().Namespace!.Contains("Notifications")))
                await handler.OnDeletingAccountAsync(ann, CancellationToken.None);
        }

        (await NotificationsOfAsync(ann.Id!)).Should().BeEmpty();
        (await DevicesOfAsync(ann.Id!)).Should().BeEmpty();
        (await LoadAsync<NotificationPreference>(NotificationPreference.IdFor(ann.Id!, null))).Should().BeNull();
        (await NotificationsOfAsync(bob.Id!)).Count.Should().Be(1, "another user's notifications stay");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static object Body(string endpoint, string p256dh, string auth) => new { endpoint, keys = new { p256dh, auth } };

    private async Task<(ECDiffieHellman Key, byte[] Auth)> SubscribeAsync(string userId, string endpoint)
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var (status, _) = await SendAsync("/spark/notifications/push/subscribe",
            Body(endpoint, Base64Url.EncodeToString(WebPushEncryptionTests.Uncompressed(key)), Base64Url.EncodeToString(auth)), userId);
        status.Should().Be(HttpStatusCode.NoContent);
        return (key, auth);
    }

    private async Task StoreAsync(object entity)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<string>> NotificationsOfAsync(string userId)
        => await QueryIdsAsync(Wire.Query(MyNotificationsQueryId), userId);

    private async Task<List<string>> DevicesOfAsync(string userId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.Advanced.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), pageSize: 1024)).Select(d => d.Id!).ToList();
    }

    /// <summary>Reads frames of <paramref name="sub"/> until one carries a total, and returns it.</summary>
    private static async Task<int> TotalAfterAsync(LiveTestClient socket, string sub, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await socket.TryNextAsync(deadline - DateTime.UtcNow);
            if (frame is not { } f || !f.TryGetProperty("sub", out var s) || s.GetString() != sub)
                continue;
            var type = f.GetProperty("type").GetString();
            if (type == "result")
                return f.GetProperty("result").GetProperty("totalItems").GetInt32();
            if (type == "patch" && f.GetProperty("patch").TryGetProperty("set", out var set) && set.TryGetProperty("totalItems", out var total))
                return total.GetInt32();
        }
        throw new TimeoutException($"No total for '{sub}' within {timeout}.");
    }

    /// <summary>A push service that records each POST and answers per endpoint (201 by default).</summary>
    internal sealed class FakePushService : HttpMessageHandler
    {
        public Dictionary<string, HttpStatusCode> StatusFor { get; } = [];

        public List<WebPushClientTests.RecordingHandler.Recorded> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var uri = request.RequestUri!.ToString();
            lock (Received)
                Received.Add(new(request.Method.Method, uri, headers, body));
            return new HttpResponseMessage(StatusFor.TryGetValue(uri, out var status) ? status : HttpStatusCode.Created);
        }
    }
}
