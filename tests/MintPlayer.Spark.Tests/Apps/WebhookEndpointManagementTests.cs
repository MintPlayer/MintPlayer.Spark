using System.Net;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Models;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 (<c>docs/apps_webhooks_PRD.md</c> D36 follow-up): what <c>/spark/apps/webhooks/endpoints</c> refuses, and how an
/// edit is stored. Every refusal is a 400 with the reason in <c>error</c>, and stores nothing.
/// </summary>
public class WebhookEndpointManagementTests : AppsTestHost
{
    protected override void ConfigureApps(SparkAppsOptions options) => options.MaxEndpointsPerOwner = 3;

    [Fact]
    public async Task A_new_webhook_is_refused_for_each_invalid_field_and_nothing_is_stored()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);
        (string What, object Body, string Error)[] cases =
        [
            ("no owner", new { url = ReceiverUrl, events = new[] { "*" } }, "ownerKind and ownerKey are required."),
            ("a relative URL", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = "/in", events = new[] { "*" } }, "The URL must be an absolute URL."),
            ("plain http", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = "http://hooks.example.com/in", events = new[] { "*" } }, "The URL must use https."),
            ("credentials in the URL", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = "https://user:pw@hooks.example.com/in", events = new[] { "*" } }, "The URL must not contain credentials."),
            ("no events", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = ReceiverUrl, events = Array.Empty<string>() }, "Pick at least one event (or * for all)."),
            ("an unknown event", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = ReceiverUrl, events = new[] { "order.shipped", "nope.happened" } }, "Unknown events: nope.happened"),
            ("a long description", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = ReceiverUrl, events = new[] { "*" }, description = new string('d', 201) }, "The description is at most 200 characters."),
        ];

        foreach (var (what, body, error) in cases)
        {
            var (status, answer) = await SendAsync("/spark/apps/webhooks/endpoints", body, ann.Id);
            status.Should().Be(HttpStatusCode.BadRequest, $"{what} is refused");
            answer.GetProperty("error").GetString().Should().Be(error, $"the refusal of {what} says why");
        }

        var (listed, list) = await GetAsync($"/spark/apps/webhooks/endpoints?ownerKind={owner.Kind}&ownerKey={Uri.EscapeDataString(owner.Key)}", ann.Id);
        listed.Should().Be(HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(0, "no refused webhook was stored");
    }

    [Fact]
    public async Task Listing_needs_an_owner_and_a_list_of_someone_elses_webhooks_is_not_found()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        (await GetAsync("/spark/apps/webhooks/endpoints", ann.Id)).Status.Should().Be(HttpStatusCode.BadRequest, "the owner is required");
        (await GetAsync($"/spark/apps/webhooks/endpoints?ownerKind=user&ownerKey={Uri.EscapeDataString(ann.Id!)}", bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob does not manage ann's webhooks, and does not learn whether she has any");
    }

    [Fact]
    public async Task An_owner_has_at_most_MaxEndpointsPerOwner_webhooks()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);
        for (var i = 0; i < 3; i++)
            await CreateEndpointAsync(ann.Id!, owner, url: $"https://hooks.example.com/{i}");

        var (status, body) = await SendAsync("/spark/apps/webhooks/endpoints", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = ReceiverUrl, events = new[] { "*" } }, ann.Id);

        status.Should().Be(HttpStatusCode.BadRequest, "the fourth webhook exceeds the limit of 3");
        body.GetProperty("error").GetString().Should().Be("An owner has at most 3 webhooks.");
    }

    [Fact]
    public async Task An_edit_is_refused_for_each_invalid_field_and_a_valid_one_is_stored_trimmed_and_deduplicated()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!), ["order.shipped"]);

        (object Body, string Error)[] refused =
        [
            (new { id = endpointId, url = "http://hooks.example.com/in" }, "The URL must use https."),
            (new { id = endpointId, events = new[] { "nope.happened" } }, "Unknown events: nope.happened"),
            (new { id = endpointId, description = new string('d', 201) }, "The description is at most 200 characters."),
        ];
        foreach (var (body, error) in refused)
        {
            var (status, answer) = await SendAsync("/spark/apps/webhooks/endpoints/update", body, ann.Id);
            status.Should().Be(HttpStatusCode.BadRequest);
            answer.GetProperty("error").GetString().Should().Be(error);
        }
        (await EndpointAsync(endpointId)).Url.Should().Be(ReceiverUrl, "a refused edit changes nothing");

        var (ok, view) = await SendAsync("/spark/apps/webhooks/endpoints/update", new
        {
            id = endpointId,
            url = "  https://hooks.example.com/moved  ",
            events = new[] { "invoice.paid", "invoice.paid", "order.shipped" },
            description = "  Billing  ",
        }, ann.Id);

        ok.Should().Be(HttpStatusCode.OK, view.ToString());
        var stored = await EndpointAsync(endpointId);
        stored.Url.Should().Be("https://hooks.example.com/moved", "the URL is trimmed");
        stored.Events.Should().Equal(["invoice.paid", "order.shipped"]);
        stored.Description.Should().Be("Billing", "the description is trimmed");
    }

    [Fact]
    public async Task Someone_who_does_not_manage_the_webhook_gets_not_found_for_every_change()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        foreach (var path in new[] { "update", "delete", "rotate-secret", "enable", "ping" })
            (await SendAsync($"/spark/apps/webhooks/endpoints/{path}", new { id = endpointId }, bob.Id)).Status
                .Should().Be(HttpStatusCode.NotFound, $"bob cannot {path} ann's webhook");
        (await GetAsync($"/spark/apps/webhooks/deliveries?endpointId={endpointId}", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync("/spark/apps/webhooks/endpoints/update", new { id = "ep_not-an-id" }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a malformed id names no webhook");
    }

    [Fact]
    public async Task A_disabled_webhook_is_neither_pinged_nor_redelivered_until_it_is_enabled()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);
        var original = (await DeliveriesAsync(endpointId)).Single();

        using (var session = Store.OpenAsyncSession())
        {
            var endpoint = await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdPrefix + endpointId);
            endpoint!.Enabled = false;
            endpoint.DisabledReason = "Switched off by the test.";
            await session.SaveChangesAsync();
        }

        var (pinged, pingError) = await SendAsync("/spark/apps/webhooks/endpoints/ping", new { id = endpointId }, ann.Id);
        pinged.Should().Be(HttpStatusCode.BadRequest);
        pingError.GetProperty("error").GetString().Should().Be("The endpoint is disabled; enable it first.");
        var (redelivered, redeliverError) = await SendAsync("/spark/apps/webhooks/deliveries/redeliver", new { id = original.PublicId }, ann.Id);
        redelivered.Should().Be(HttpStatusCode.BadRequest);
        redeliverError.GetProperty("error").GetString().Should().Be("The endpoint is disabled; enable it first.");
        (await DeliveriesAsync(endpointId)).Should().HaveCount(1, "nothing was scheduled while it is disabled");

        (await SendAsync("/spark/apps/webhooks/endpoints/enable", new { id = endpointId }, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync("/spark/apps/webhooks/deliveries/redeliver", new { id = original.PublicId }, ann.Id)).Status
            .Should().Be(HttpStatusCode.OK, "enabled again, the delivery can be sent again");
    }

    [Fact]
    public async Task Redelivery_and_the_delivery_log_refuse_ids_that_name_nothing()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        (await SendAsync("/spark/apps/webhooks/deliveries/redeliver", new { id = "not-a-delivery" }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a malformed delivery id names nothing");
        var wellFormed = $"{endpointId}/{new string('0', 16)}_msg_{new string('a', 32)}";
        (await SendAsync("/spark/apps/webhooks/deliveries/redeliver", new { id = wellFormed }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a well-formed id of a delivery that was never logged");

        var (status, body) = await GetAsync($"/spark/apps/webhooks/deliveries?endpointId={endpointId}&after=garbage", ann.Id);
        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().Should().Be("after is not a delivery id.");
    }
}
