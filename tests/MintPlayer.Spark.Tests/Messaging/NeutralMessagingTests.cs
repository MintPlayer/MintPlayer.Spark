using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// #536 D40: messaging end to end on the neutral driver: the database <c>SPARK_TEST_DATABASE</c> picks (RavenDB,
/// SQL Server, PostgreSQL, InMemory), with no line of provider code. A message is published, fed through the store's
/// subscription (D39), claimed, handled, and completed, through the bus and through the outbox. A handler that fails
/// once is parked, woken by the retry sweeper and completed on its second attempt.
/// </summary>
public class NeutralMessagingTests : SparkTestDriver
{
    private const string Queue = "neutral-messaging";

    public sealed class MessagingContext : SparkContext;

    [MessageQueue(Queue)]
    public sealed class Ping
    {
        public string Text { get; set; } = "";
        public bool FailOnce { get; set; }
    }

    public sealed class Handled
    {
        public ConcurrentQueue<string> Texts { get; } = new();
        public ConcurrentDictionary<string, int> Attempts { get; } = new();
    }

    public sealed class PingRecipient(Handled handled) : IRecipient<Ping>
    {
        public Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            var attempt = handled.Attempts.AddOrUpdate(message.Text, 1, (_, n) => n + 1);
            if (message.FailOnce && attempt == 1)
                throw new InvalidOperationException("The first attempt fails on purpose.");
            handled.Texts.Enqueue(message.Text);
            return Task.CompletedTask;
        }
    }

    private SparkEndpointFactory<MessagingContext> NewHost(Handled handled) => new(
        Database,
        [],
        configureServices: services =>
        {
            services.AddSingleton(handled);
            services.AddScoped<IRecipient<Ping>, PingRecipient>();
        },
        configureSpark: spark => spark.AddMessaging(o =>
        {
            o.FallbackPollInterval = TimeSpan.FromMilliseconds(500);
            o.BackoffDelays = [TimeSpan.FromMilliseconds(200)];
        }));

    private static async Task<IReadOnlyList<SparkMessage>> MessagesAsync(SparkEndpointFactory<MessagingContext> host)
    {
        using var session = host.GetService<ISparkStore>().OpenSession();
        return await session.ToListAsync(session.Query<SparkMessage>());
    }

    private static async Task CompletedAsync(SparkEndpointFactory<MessagingContext> host, int count, string what)
        => await AsyncWait.ForAsync(
            () => MessagesAsync(host)!,
            messages => messages.Count(m => m.Status == EMessageStatus.Completed) >= count,
            what,
            messages => string.Join(", ", (messages ?? []).Select(m => $"{m.Id}={m.Status}/{m.AttemptCount}")),
            TimeSpan.FromSeconds(60));

    [Fact]
    public async Task A_broadcast_message_is_handled_and_completed()
    {
        var handled = new Handled();
        await using var host = NewHost(handled);

        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().BroadcastAsync(new Ping { Text = "bus" });

        await AsyncWait.UntilAsync(() => handled.Texts.Contains("bus"), "the message to be handled", TimeSpan.FromSeconds(60));
        await CompletedAsync(host, 1, "the message to be completed");

        var message = (await MessagesAsync(host)).Single();
        message.QueueName.Should().Be(Queue);
        message.QueuedAtUtc.Should().HaveValue("the feeder captures the sequence on its claim");
        message.AttemptCount.Should().Be(1);

        // D42: the page and the sweeper query the declared view; a collection query would make RavenDB build an auto
        // index, which an app cannot deploy ahead of time (CodeCoverage D-O22).
        if (host.GetService<IServiceProvider>().GetService<Raven.Client.Documents.IDocumentStore>() is { } raven)
        {
            var names = await raven.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.GetIndexNamesOperation(0, 1024));
            names.Should().NotContain(n => n.StartsWith("Auto/SparkMessages", StringComparison.OrdinalIgnoreCase));
            names.Should().Contain(nameof(SparkMessages_ByState));
        }
    }

    [Fact]
    public async Task An_outbox_message_is_published_by_the_callers_save_and_handled()
    {
        var handled = new Handled();
        await using var host = NewHost(handled);

        using (var scope = host.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<ISparkSession>();
            await scope.ServiceProvider.GetRequiredService<IMessageOutbox>().EnqueueAsync(new Ping { Text = "outbox" });
            (await MessagesAsync(host)).Should().BeEmpty("only stored, not saved");
            await session.SaveChangesAsync();
        }

        await AsyncWait.UntilAsync(() => handled.Texts.Contains("outbox"), "the message to be handled", TimeSpan.FromSeconds(60));
        await CompletedAsync(host, 1, "the message to be completed");
    }

    [Fact]
    public async Task A_failed_attempt_is_retried_by_the_sweeper_and_completes()
    {
        var handled = new Handled();
        await using var host = NewHost(handled);

        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().BroadcastAsync(new Ping { Text = "retry", FailOnce = true });

        await AsyncWait.UntilAsync(() => handled.Texts.Contains("retry"), "the second attempt to succeed", TimeSpan.FromSeconds(60));
        await CompletedAsync(host, 1, "the message to be completed");

        handled.Attempts["retry"].Should().Be(2);
        (await MessagesAsync(host)).Single().AttemptCount.Should().Be(2);
    }
}
