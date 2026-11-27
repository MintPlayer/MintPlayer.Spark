using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Messaging;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #536 D50: a chain write that loses to a concurrent one is retried in the same scope. <c>Clear()</c> forgets the stale
/// chain and the attempt's pending writes, so the retry reads the chain again and builds on the other writer's change.
/// Without it the retry saves the same stale chain, and every attempt conflicts.
/// </summary>
public class WebhookChainRetryTests : AppsTestHost
{
    private const string Marker = "webhookdeliveries/concurrent-marker";

    protected override void ConfigureApps(SparkAppsOptions options) => options.Ordering = WebhookOrdering.PerDestination;

    [Fact]
    public async Task A_fan_out_that_loses_the_chain_to_a_concurrent_write_reads_it_again_and_schedules_once()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, owner);
        var release = HoldDeliveries();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var message = new WebhookEventRaised
            {
                MessageId = WebhookPublisher.NewMessageId(now),
                EventType = "order.shipped",
                OwnerKind = owner.Kind,
                OwnerKey = owner.Key,
                Payload = WebhookPublisher.Envelope("order.shipped", now, JsonSerializer.SerializeToNode(new OrderShipped("o-1", 1, null)), 2048),
                CreatedUtc = now.UtcDateTime,
            };

            var raced = await WithConcurrentChainWritesAsync(endpointId, chain => chain.Waiting.Add(Marker), () => FanOutAsync(message));

            raced.Should().Be(1, "the concurrent write ran inside the fan-out's save");
            var deliveries = await DeliveriesAsync(endpointId);
            deliveries.Should().ContainSingle("the retry schedules the event once");
            var stored = await ChainAsync(endpointId);
            stored.InFlight.Should().BeEquivalentTo(deliveries[0].Id.Value, "the retry started the delivery");
            stored.Waiting.Should().Equal([Marker], "the retry read the chain again instead of overwriting the other write");
        }
        finally
        {
            release();
        }
    }

    [Fact]
    public async Task A_completion_that_loses_the_chain_to_a_concurrent_write_reads_it_again_and_starts_the_next()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        var release = HoldDeliveries();
        try
        {
            var created = DateTime.UtcNow;
            var first = Delivery(endpointId, created, "msg_first");
            var second = Delivery(endpointId, created.AddSeconds(1), "msg_second");
            using (var seed = Store.OpenAsyncSession())
            {
                await seed.StoreAsync(first, first.Id.Value);
                await seed.StoreAsync(second, second.Id.Value);
                var chainId = WebhookChain.IdFor(endpointId);
                await seed.StoreAsync(new WebhookChain { Id = chainId, InFlight = first.Id.Value, Waiting = [second.Id.Value] }, chainId.Value);
                await seed.SaveChangesAsync();
            }

            var raced = await WithConcurrentChainWritesAsync(endpointId, chain => chain.Waiting.Add(Marker), async () =>
            {
                using var scope = Factory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebhookScheduler>().CompleteAsync(first, halt: false, CancellationToken.None);
            });

            raced.Should().Be(1, "the concurrent write ran inside the completion's save");
            var stored = await ChainAsync(endpointId);
            stored.InFlight.Should().BeEquivalentTo(second.Id.Value, "the retry moved the chain on to the next delivery");
            stored.Waiting.Should().Equal([Marker], "the retry read the chain again instead of overwriting the other write");
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// Before #536 D50 every attempt opened a session of its own, with RavenDB's 30 requests. On the scope's session the
    /// attempts share one count (<c>Clear()</c> keeps it), so eight lost races (nine attempts of about five requests) must not
    /// end the fan-out on the request budget.
    /// </summary>
    [Fact]
    public async Task A_fan_out_that_loses_the_chain_eight_times_is_not_stopped_by_the_session_request_budget()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, owner);
        var release = HoldDeliveries();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var message = new WebhookEventRaised
            {
                MessageId = WebhookPublisher.NewMessageId(now),
                EventType = "order.shipped",
                OwnerKind = owner.Kind,
                OwnerKey = owner.Key,
                Payload = WebhookPublisher.Envelope("order.shipped", now, JsonSerializer.SerializeToNode(new OrderShipped("o-1", 1, null)), 2048),
                CreatedUtc = now.UtcDateTime,
            };
            var markers = Enumerable.Range(1, 8).Select(i => $"{Marker}-{i}").ToList();
            var written = 0;

            var raced = await WithConcurrentChainWritesAsync(endpointId, chain => chain.Waiting.Add(markers[written++]), () => FanOutAsync(message), times: 8);

            raced.Should().Be(8, "every attempt but the last lost the chain");
            var deliveries = await DeliveriesAsync(endpointId);
            deliveries.Should().ContainSingle("the ninth attempt scheduled the event, once");
            var stored = await ChainAsync(endpointId);
            stored.InFlight.Should().BeEquivalentTo(deliveries[0].Id.Value, "the last attempt started the delivery");
            stored.Waiting.Should().Equal(markers, "every attempt read the chain again");
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// A chain change can run late in its scope: <see cref="WebhookEndpointManager.EnableAsync"/> resumes the chain in the
    /// request's session, after its own reads and write. A retry must not end on the requests that session has left.
    /// </summary>
    [Fact]
    public async Task A_completion_retried_late_in_its_scope_is_not_stopped_by_the_session_request_budget()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        var release = HoldDeliveries();
        try
        {
            var created = DateTime.UtcNow;
            var first = Delivery(endpointId, created, "msg_first");
            var second = Delivery(endpointId, created.AddSeconds(1), "msg_second");
            using (var seed = Store.OpenAsyncSession())
            {
                await seed.StoreAsync(first, first.Id.Value);
                await seed.StoreAsync(second, second.Id.Value);
                var chainId = WebhookChain.IdFor(endpointId);
                await seed.StoreAsync(new WebhookChain { Id = chainId, InFlight = first.Id.Value, Waiting = [second.Id.Value] }, chainId.Value);
                await seed.SaveChangesAsync();
            }

            var raced = await WithConcurrentChainWritesAsync(endpointId, chain => chain.Waiting.Add(Marker), async () =>
            {
                using var scope = Factory.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<ISparkSession>();
                // The scope's earlier work leaves four requests: one attempt (load, load, save), not two.
                while (session.NumberOfRequests < session.MaxNumberOfRequests - 4)
                    await session.LoadAsync<WebhookDelivery>($"webhookdeliveries/elsewhere-{session.NumberOfRequests}");
                await scope.ServiceProvider.GetRequiredService<WebhookScheduler>().CompleteAsync(first, halt: false, CancellationToken.None);
            });

            raced.Should().Be(1, "the concurrent write ran inside the completion's save");
            var stored = await ChainAsync(endpointId);
            stored.InFlight.Should().BeEquivalentTo(second.Id.Value, "the retry moved the chain on to the next delivery");
            stored.Waiting.Should().Equal([Marker]);
        }
        finally
        {
            release();
        }
    }

    private async Task FanOutAsync(WebhookEventRaised message)
    {
        using var scope = Factory.CreateScope();
        var fanOut = scope.ServiceProvider.GetServices<IRecipient<WebhookEventRaised>>().OfType<WebhookFanOut>().Single();
        await fanOut.HandleAsync(message);
    }

    private static WebhookDelivery Delivery(string endpointId, DateTime created, string messageId) => new()
    {
        Id = WebhookDelivery.IdFor(endpointId, created, messageId),
        MessageId = messageId,
        EventType = "order.shipped",
        EndpointId = endpointId,
        Payload = "{}",
        Status = WebhookDeliveryStatus.Pending,
        CreatedUtc = created,
        Ordered = true,
    };

    /// <summary>
    /// Runs <paramref name="act"/>; the first <paramref name="times"/> times its session writes the chain, another session
    /// changes the stored chain first (<paramref name="change"/>), so that write conflicts. Returns how many times the
    /// interleaved write ran.
    /// </summary>
    private async Task<int> WithConcurrentChainWritesAsync(string endpointId, Action<WebhookChain> change, Func<Task> act, int times = 1)
    {
        var chainId = WebhookChain.IdFor(endpointId).Value;
        var raced = 0;
        var interleaving = false;
        void Interleave(object? sender, BeforeStoreEventArgs e)
        {
            // The other session's own write raises the event too, while this one waits for it.
            if (interleaving || raced == times || !string.Equals(e.DocumentId, chainId, StringComparison.OrdinalIgnoreCase))
                return;
            interleaving = true;
            try
            {
                Task.Run(async () =>
                {
                    using var other = Store.OpenAsyncSession();
                    var chain = await other.LoadAsync<WebhookChain>(chainId);
                    if (chain is null)
                    {
                        chain = new WebhookChain { Id = WebhookChain.IdFor(endpointId) };
                        await other.StoreAsync(chain, chainId);
                    }
                    change(chain);
                    await other.SaveChangesAsync();
                }).GetAwaiter().GetResult();
                raced++;
            }
            finally
            {
                interleaving = false;
            }
        }

        Store.OnBeforeStore += Interleave;
        try
        {
            await act();
        }
        finally
        {
            Store.OnBeforeStore -= Interleave;
        }
        return raced;
    }

    private async Task<WebhookChain> ChainAsync(string endpointId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<WebhookChain>(WebhookChain.IdFor(endpointId).Value))!;
    }

    /// <summary>Holds every delivery at the receiver, so none completes (and moves the chain) while a test reads it.</summary>
    private Action HoldDeliveries()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Receiver.Forward = async _ =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        return () => release.TrySetResult();
    }
}
