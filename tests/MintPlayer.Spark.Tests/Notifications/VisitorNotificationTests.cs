using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Live;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Notifications;

/// <summary>
/// Notifications for anonymous visitors (<c>docs/notification_bell_PRD.md</c> G4, R3–R8): addressed by the visitor id
/// <see cref="ISparkCurrentPrincipal.EnsureVisitorId"/> issued, read only by that browser (over HTTP and
/// <c>/spark/live</c>), in-app only, expiring with the visitor, and moved to the account on sign-in. The slot is bound to
/// the anonymous and the signed-in group, as DemoApp binds it.
/// </summary>
public class VisitorNotificationTests : OrganizationsTestHost
{
    private static readonly Uri Server = new("http://localhost/");

    public VisitorNotificationTests()
    {
        ExtraLibrarySlots["notifications"] = "notifications:users";
        SlotGroups["notifications:users"] = ["Anonymous visitors", "Signed-in users"];
        ExtraSpark = spark => spark.AddNotifications(o => o.PublicBaseUrl = PublicBaseUrl);
    }

    private ISparkNotifier Notifier() => Factory.CreateScope().ServiceProvider.GetRequiredService<ISparkNotifier>();

    [Fact]
    public async Task A_visitor_notification_is_read_only_by_its_visitor_and_expires_with_the_visitor()
    {
        var a = NewVisitor();
        var b = NewVisitor();
        var ann = await SeedUserAsync("ann");

        var before = DateTime.UtcNow;
        var receipt = await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [a.Id], Title = "Your report is ready", Url = "/reports/1" });

        (receipt.Mails, receipt.Pushes).Should().Be((0, 0), "visitors get in-app only");
        var id = receipt.NotificationIds.Should().ContainSingle().Which;
        var stored = await LoadAsync<Notification>(id);
        stored!.VisitorId.Should().Be(a.Id);
        stored.UserId.Should().BeNull();
        stored.ExpiresAtUtc!.Value.Should().BeCloseTo(before + TimeSpan.FromHours(24), TimeSpan.FromMinutes(1), "Spark:Visitor:Lifetime, not the model's 90 days");

        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), null!, a.Cookie)).Should().Equal(id);
        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), null!, b.Cookie)).Should().BeEmpty();
        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), ann.Id!)).Should().BeEmpty();
        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), null!)).Should().BeEmpty("no identity: the closed row filter");

        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTests.NotificationTypeId, id: id), null, a.Cookie)).Status.Should().Be(HttpStatusCode.OK);
        // Another visitor is anonymous, and an anonymous denial is the uniform 401; a signed-in one is 404.
        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTests.NotificationTypeId, id: id), null, b.Cookie)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTests.NotificationTypeId, id: id), ann.Id)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_user_notification_is_not_readable_by_a_visitor()
    {
        var a = NewVisitor();
        var ann = await SeedUserAsync("ann");

        var id = (await Notifier().NotifyAsync(new SparkNotification { UserIds = [ann.Id!], Title = "x" })).NotificationIds.Single();

        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), null!, a.Cookie)).Should().BeEmpty();
        (await SendAsync("/spark/po/load", Wire.Typed(NotificationTests.NotificationTypeId, id: id), null, a.Cookie)).Status.Should().Be(HttpStatusCode.Unauthorized, "an anonymous denial is the uniform 401");
    }

    [Theory]
    [InlineData("SparkUsers/ann")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public async Task A_visitor_id_without_its_prefix_addresses_nobody(string id)
    {
        var receipt = await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [id], Title = "x" });

        receipt.NotificationIds.Should().BeEmpty("a user id can never be addressed as a visitor");
    }

    [Fact]
    public async Task Mark_read_serves_a_visitor_and_touches_only_their_own()
    {
        var a = NewVisitor();
        var b = NewVisitor();
        var aId = (await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [a.Id], Title = "a" })).NotificationIds.Single();
        var bId = (await NotifyTwiceAsync(b.Id))[0];

        var (status, body) = await SendAsync("/spark/notifications/read", new { ids = new[] { aId, bId } }, null, a.Cookie);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("marked").GetInt32().Should().Be(1);
        (await LoadAsync<Notification>(aId))!.IsRead.Should().BeTrue();
        (await LoadAsync<Notification>(bId))!.IsRead.Should().BeFalse();

        // "Mark all" for a fresh visitor C marks nothing of B's.
        var c = NewVisitor();
        (await SendAsync("/spark/notifications/read", new { ids = (string[]?)null }, null, c.Cookie)).Body.GetProperty("marked").GetInt32().Should().Be(0);
        (await LoadAsync<Notification>(bId))!.IsRead.Should().BeFalse();
        // "Mark all" queries the index, which may lag the just-stored documents by milliseconds; marking is idempotent.
        var marked = 0;
        for (var attempt = 0; attempt < 50 && marked < 2; attempt++)
        {
            marked += (await SendAsync("/spark/notifications/read", new { ids = (string[]?)null }, null, b.Cookie)).Body.GetProperty("marked").GetInt32();
            if (marked < 2)
                await Task.Delay(100);
        }
        marked.Should().Be(2);

        (await SendAsync("/spark/notifications/read", new { ids = new[] { aId } }, null)).Status.Should().Be(HttpStatusCode.Unauthorized, "neither a user nor a visitor");
    }

    [Fact]
    public async Task Mark_read_by_a_visitor_needs_the_antiforgery_token()
    {
        var a = NewVisitor();
        var aId = (await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [a.Id], Title = "a" })).NotificationIds.Single();

        using var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/notifications/read") { Content = JsonContent.Create(new { ids = new[] { aId } }) };
        request.Headers.Add("Cookie", a.Cookie);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK, "the visitor cookie is an ambient credential (PRD S5): a cross-site POST must not mark");
        (await LoadAsync<Notification>(aId))!.IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task The_push_endpoints_still_need_a_user()
    {
        var a = NewVisitor();

        (await SendAsync("/spark/notifications/push/subscribe", new { endpoint = "https://fcm.googleapis.com/fcm/send/x", keys = new { p256dh = "x", auth = "y" } }, null, a.Cookie))
            .Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync("/spark/notifications/push/unsubscribe", new { endpoint = "https://fcm.googleapis.com/fcm/send/x" }, null, a.Cookie))
            .Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_live_socket_of_a_visitor_sees_their_notification_and_never_another_visitors()
    {
        var a = NewVisitor();
        var b = NewVisitor();
        await using var socket = await LiveTestClient.ConnectAsync(Factory.CreateWebSocketClient(), Server, r => r.Headers["Cookie"] = b.Cookie);
        await socket.SubscribeQueryAsync("mine", new { queryId = NotificationTests.MyNotificationsQueryId, skip = 0, take = 5 });
        var first = await socket.NextAsync("result", "mine");
        first.GetProperty("result").GetProperty("totalItems").GetInt32().Should().Be(0);

        // A's notification first: had it leaked to B's socket, its frame would arrive before B's own.
        await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [a.Id], Title = "for-visitor-a" });
        await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [b.Id], Title = "for-visitor-b" });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var seen = new List<string>();
        while (DateTime.UtcNow < deadline)
        {
            if (await socket.TryNextAsync(deadline - DateTime.UtcNow) is not { } frame)
                continue;
            var raw = frame.GetRawText();
            seen.Add(raw);
            if (raw.Contains("for-visitor-b"))
                break;
        }

        seen.Should().Contain(f => f.Contains("for-visitor-b"), "B's own notification reaches B's socket");
        seen.Should().NotContain(f => f.Contains("for-visitor-a"), "A's never does");
    }

    [Fact]
    public async Task Signing_in_moves_the_visitors_notifications_to_the_account_and_clears_the_cookies()
    {
        var a = NewVisitor();
        var other = NewVisitor();
        var ann = await SeedUserAsync("ann");
        var ids = await NotifyTwiceAsync(a.Id);
        var othersId = (await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [other.Id], Title = "other" })).NotificationIds.Single();

        var setCookies = await SignInAsync(ann, a.Cookie);

        foreach (var id in ids)
        {
            var moved = await LoadAsync<Notification>(id);
            moved!.UserId.Should().Be(ann.Id);
            moved.VisitorId.Should().BeNull();
            moved.ExpiresAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow + TimeSpan.FromDays(90), TimeSpan.FromMinutes(5), "the model's expiresAfter, as for a user");
        }
        (await LoadAsync<Notification>(othersId))!.VisitorId.Should().Be(other.Id, "another visitor's stay");
        setCookies.Should().Contain(c => c.StartsWith(SparkVisitorOptions.CookieName + "=;", StringComparison.Ordinal));
        setCookies.Should().Contain(c => c.StartsWith(SparkVisitorOptions.PresenceCookieName + "=;", StringComparison.Ordinal));
        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), ann.Id!)).Should().BeEquivalentTo(ids);

        // Idempotent: a second sign-in with the same (stale) cookie finds nothing left.
        await SignInAsync(ann, a.Cookie);
        (await QueryIdsAsync(Wire.Query(NotificationTests.MyNotificationsQueryId), ann.Id!)).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task Signing_in_without_a_visitor_changes_nothing()
    {
        var ann = await SeedUserAsync("ann");

        var setCookies = await SignInAsync(ann, cookie: null);

        setCookies.Should().NotContain(c => c.StartsWith(SparkVisitorOptions.CookieName, StringComparison.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private sealed record Visitor(string Id, string Cookie);

    /// <summary>A visitor issued by the host's own principal, as an app's request issues one.</summary>
    private Visitor NewVisitor()
    {
        using var scope = Factory.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var id = scope.ServiceProvider.GetRequiredService<ISparkCurrentPrincipal>().EnsureVisitorId()!;
        id.Should().StartWith(SparkVisitorOptions.IdPrefix);
        var cookie = string.Join("; ", http.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]));
        return new Visitor(id, cookie);
    }

    private async Task<List<string>> NotifyTwiceAsync(string visitorId)
    {
        var first = await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [visitorId], Title = "one" });
        var second = await Notifier().NotifyAsync(new SparkNotification { VisitorIds = [visitorId], Title = "two" });
        return [.. first.NotificationIds, .. second.NotificationIds];
    }

    /// <summary>Signs in through Spark's sign-in manager, as every sign-in path ends, in a request carrying <paramref name="cookie"/>.</summary>
    private async Task<List<string>> SignInAsync(SparkUser user, string? cookie)
    {
        using var scope = Factory.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (cookie is not null)
            http.Request.Headers.Cookie = cookie;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<SparkUser>>();
        var stored = await scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>().FindByIdAsync(user.Id!);
        await signIn.SignInAsync(stored!, isPersistent: false);
        return [.. http.Response.Headers.SetCookie.Select(c => c!)];
    }
}
