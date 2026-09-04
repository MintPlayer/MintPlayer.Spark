using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Recovery for messages whose host died mid-handler.
/// </summary>
/// <remarks>
/// A message is set to <c>Processing</c> before its handlers run. If the process dies there, nothing
/// moves it: it is neither <c>Pending</c> nor <c>Failed</c>, so no drain selects it, and under ordered
/// delivery its partition stays blocked behind work that will never finish. That was already true
/// before this refactor and simply had no owner.
/// </remarks>
public class MessageReaperTests : SparkTestDriver
{
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    [Fact]
    public async Task A_message_stranded_in_Processing_past_the_lease_is_returned_to_the_queue()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new SparkMessagingOptions { ProcessingLease = TimeSpan.FromMinutes(30) };

        var stranded = new SparkMessage
        {
            QueueName = "reaper-lane",
            MessageType = "irrelevant",
            PayloadJson = "{}",
            Status = EMessageStatus.Processing,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime.AddHours(-2),
            Sequence = 1,
        };

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(stranded);
            await session.SaveChangesAsync();
        }

        await Store.WaitForIndexingAsync();

        // Move the clock past the lease. The document's last-modified stamp is real, so the lease has
        // to be measured against a clock that has moved beyond it.
        clock.Advance(TimeSpan.FromHours(2));

        var reaper = new MessageReaper(
            Store, Options.Create(options), clock, NullLogger<MessageReaper>.Instance);

        var reaped = await reaper.ReapAsync(CancellationToken.None);

        reaped.Should().Be(1);

        using var verify = Store.OpenAsyncSession();
        var recovered = await verify.LoadAsync<SparkMessage>(stranded.Id);
        recovered.Status.Should().Be(EMessageStatus.Failed, "a reaped message must become selectable again");
        recovered.NextAttemptAtUtc.Should().NotHaveValue("it is due immediately, not parked");
        recovered.AttemptCount.Should().Be(1,
            "the attempt must be counted, or a message that reliably kills its host would loop forever");
    }

    [Fact]
    public async Task A_message_still_within_its_lease_is_left_alone()
    {
        // Reaping too eagerly is worse than reaping late: it double-processes work that is still
        // running, and a long handler — parsing a whole coverage report — is normal.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new SparkMessagingOptions { ProcessingLease = TimeSpan.FromMinutes(30) };

        var running = new SparkMessage
        {
            QueueName = "reaper-lane",
            MessageType = "irrelevant",
            PayloadJson = "{}",
            Status = EMessageStatus.Processing,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime.AddHours(-2),
            Sequence = 1,
        };

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(running);
            await session.SaveChangesAsync();
        }

        await Store.WaitForIndexingAsync();

        // Only five minutes pass, so the document was modified well within the lease even though it
        // was *created* two hours ago — which is why the lease is measured from last-modified, not
        // from CreatedAtUtc.
        clock.Advance(TimeSpan.FromMinutes(5));

        var reaper = new MessageReaper(
            Store, Options.Create(options), clock, NullLogger<MessageReaper>.Instance);

        (await reaper.ReapAsync(CancellationToken.None)).Should().Be(0);

        using var verify = Store.OpenAsyncSession();
        var untouched = await verify.LoadAsync<SparkMessage>(running.Id);
        untouched.Status.Should().Be(EMessageStatus.Processing);
    }
}
