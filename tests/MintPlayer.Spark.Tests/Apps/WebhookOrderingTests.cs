using System.Net;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Models;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 S9 / D22: with <see cref="WebhookOrdering.PerDestination"/> one delivery is in flight per endpoint, and the
/// next starts only when it succeeded or was given up.
/// </summary>
public class WebhookOrderingTests : AppsTestHost
{
    protected override void ConfigureApps(SparkAppsOptions options)
    {
        options.Ordering = WebhookOrdering.PerDestination;
        // o-1's three failures would otherwise open the breaker, and o-2 would be given up unsent (correctly, but not
        // what these tests are about).
        options.BreakerThreshold = 100;
    }

    [Fact]
    public async Task Events_arrive_in_publish_order_even_when_the_first_needs_a_retry()
    {
        var ann = await SeedUserAsync("ann");
        // The first request fails, so event 1 sits in its retry delay while 2 and 3 are already published.
        Receiver.Respond = (_, n) => n == 0 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        foreach (var order in new[] { "o-1", "o-2", "o-3" })
            await PublishAsync(new OrderShipped(order, 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 3);

        Receiver.Received.Select(r => r.Json.GetProperty("data").GetProperty("orderId").GetString())
            .Should().Equal(["o-1", "o-1", "o-2", "o-3"], "nothing overtakes the event in flight");
        (await DeliveriesAsync(endpointId)).Should().AllSatisfy(d => d.Status.Should().Be(WebhookDeliveryStatus.Succeeded));
    }

    [Fact]
    public async Task By_default_a_given_up_event_is_skipped_and_the_chain_moves_on()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (r, _) => r.Json.GetProperty("data").GetProperty("orderId").GetString() == "o-1" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await PublishAsync(new OrderShipped("o-2", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 2);

        Receiver.Received.Select(r => r.Json.GetProperty("data").GetProperty("orderId").GetString()).Should().Equal(["o-1", "o-1", "o-1", "o-2"]);
        (await EndpointAsync(endpointId)).Enabled.Should().BeTrue();
    }
}

/// <summary>D22 with <see cref="WebhookExhaustedPolicy.Halt"/>: the chain stops on a given-up event until the endpoint is enabled.</summary>
public class WebhookOrderingHaltTests : AppsTestHost
{
    protected override void ConfigureApps(SparkAppsOptions options)
    {
        options.Ordering = WebhookOrdering.PerDestination;
        options.OnExhausted = WebhookExhaustedPolicy.Halt;
    }

    [Fact]
    public async Task A_halted_chain_waits_and_enabling_the_endpoint_retries_the_failed_event_first()
    {
        var ann = await SeedUserAsync("ann");
        var failing = true;
        Receiver.Respond = (_, _) => Volatile.Read(ref failing) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await PublishAsync(new OrderShipped("o-2", 1, null), WebhookOwner.User(ann.Id!));
        await UntilDisabledHandlerToldAsync(endpointId);

        (await EndpointAsync(endpointId)).Enabled.Should().BeFalse("the halt disables the endpoint");
        Receiver.Received.Select(r => r.Json.GetProperty("data").GetProperty("orderId").GetString()).Should().Equal(["o-1", "o-1", "o-1"], "o-2 waits behind the halted o-1");
        DisabledCount(endpointId).Should().Be(1);

        Volatile.Write(ref failing, false);
        (await SendAsync("/spark/apps/webhooks/endpoints/enable", new { id = endpointId }, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        await Until(async () => (await DeliveriesAsync(endpointId)).All(d => d.Status == WebhookDeliveryStatus.Succeeded), "both events delivered after enabling");

        Receiver.Received.Select(r => r.Json.GetProperty("data").GetProperty("orderId").GetString()).Should().Equal(["o-1", "o-1", "o-1", "o-1", "o-2"]);
    }
}
