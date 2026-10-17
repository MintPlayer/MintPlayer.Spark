using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// P18 (dependency-updates PRD §13.13): a reclaim — the host died mid-handler and its claim lapsed — is a
/// failed attempt. It used to increment only <see cref="SparkMessage.AttemptCount"/>, which the dead-letter
/// decision never read (it reads the handlers' counts), so a message that kills its host every time was
/// handed to the next host for ever, despite the comment in <c>MessageRetrySweeper.ReclaimAbandonedAsync</c>.
/// <para>
/// The crash is simulated by document state, which is all the next host sees of it: handlers populated on
/// the first pickup, the message still at <c>Processing</c> on a claim that has lapsed.
/// </para>
/// </summary>
public class MessageReclaimDeadLetterTests : SparkTestDriver
{
    private const string Owner = "this-node/0001";

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(SparkMessages_ByQueue).Assembly];

    public sealed class Crash { public string? Text { get; set; } }

    public sealed class CrashRecipient : IRecipient<Crash>
    {
        public int Calls;

        public Task HandleAsync(Crash message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    public sealed class FailingRecipient : IRecipient<Crash>
    {
        public int Calls;

        public Task HandleAsync(Crash message, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("relay down");
        }
    }

    private sealed class AllowAll : IMessageTypeAllowList
    {
        public bool IsAllowedMessageType(string? assemblyQualifiedName) => true;
        public bool IsAllowedHandlerType(string? assemblyQualifiedName) => true;
    }

    private static ServiceProvider Services<TRecipient>(TRecipient recipient) where TRecipient : class, IRecipient<Crash>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageTypeAllowList>(new AllowAll());
        services.AddSingleton<IRecipient<Crash>>(recipient);
        return services.BuildServiceProvider();
    }

    private MessageProcessor NewProcessor(IServiceProvider services)
        => new(services, Store, Options.Create(new SparkMessagingOptions()), NullLogger<MessageProcessor>.Instance);

    private MessageRetrySweeper NewSweeper()
        => new(
            Store,
            Options.Create(new SparkMessagingOptions()),
            NullLogger<MessageRetrySweeper>.Instance,
            new MessagingLeaseManager(Store, NullLogger<MessagingLeaseManager>.Instance) { IsHeld = true });

    private async Task<string> SeedAsync<TRecipient>(int maxAttempts)
    {
        var message = new SparkMessage
        {
            QueueName = "reclaim-tests",
            MessageType = typeof(Crash).AssemblyQualifiedName!,
            PayloadJson = JsonConvert.SerializeObject(new Crash { Text = "boom" }),
            CreatedAtUtc = DateTime.UtcNow,
            MaxAttempts = maxAttempts,
            AttemptCount = 0,
            Status = EMessageStatus.Pending,
            Handlers =
            [
                new HandlerExecution { HandlerType = typeof(TRecipient).AssemblyQualifiedName!, Status = EHandlerStatus.Pending },
            ],
        };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(message);
        await session.SaveChangesAsync();
        return message.Id!;
    }

    /// <summary>Claims the message as the feeder does, with a claim that may already have lapsed.</summary>
    private async Task ClaimAsync(string id, DateTime claimExpiresAtUtc)
    {
        using var session = Store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(id);
        message.Status = EMessageStatus.Processing;
        message.OwnerId = Owner;
        message.ClaimExpiresAtUtc = claimExpiresAtUtc;
        message.AttemptCount++;
        message.WakeUp = false;
        await session.SaveChangesAsync();
    }

    /// <summary>The host dies mid-handler: the claim lapses and the sweeper hands the message back.</summary>
    private async Task CrashAndReclaimAsync(string id)
    {
        await ClaimAsync(id, DateTime.UtcNow.AddMinutes(-1));
        await Store.WaitForIndexingAsync();
        (await NewSweeper().ReclaimAbandonedAsync(CancellationToken.None)).Should().Be(1);
    }

    private async Task<SparkMessage> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<SparkMessage>(id);
    }

    [Fact]
    public async Task A_message_that_kills_its_host_MaxAttempts_times_is_dead_lettered_instead_of_run_again()
    {
        var recipient = new CrashRecipient();
        using var services = Services(recipient);
        var id = await SeedAsync<CrashRecipient>(maxAttempts: 3);

        for (var crash = 0; crash < 3; crash++)
            await CrashAndReclaimAsync(id);

        await ClaimAsync(id, DateTime.UtcNow.AddMinutes(5));
        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var message = await LoadAsync(id);
        recipient.Calls.Should().Be(0, "three crashes are three failed attempts, and MaxAttempts is 3");
        message.Status.Should().Be(EMessageStatus.DeadLettered);
        message.DeadLetterReason.Should().Be(EDeadLetterReason.MaxAttempts);
        message.Handlers.Should().OnlyContain(h => h.Status == EHandlerStatus.DeadLettered);
    }

    [Fact]
    public async Task A_message_reclaimed_fewer_than_MaxAttempts_times_still_runs()
    {
        var recipient = new CrashRecipient();
        using var services = Services(recipient);
        var id = await SeedAsync<CrashRecipient>(maxAttempts: 3);

        for (var crash = 0; crash < 2; crash++)
            await CrashAndReclaimAsync(id);

        await ClaimAsync(id, DateTime.UtcNow.AddMinutes(5));
        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        recipient.Calls.Should().Be(1);
        (await LoadAsync(id)).Status.Should().Be(EMessageStatus.Completed);
    }

    [Fact]
    public async Task Reclaims_and_genuine_failures_share_one_budget()
    {
        var recipient = new FailingRecipient();
        using var services = Services(recipient);
        var id = await SeedAsync<FailingRecipient>(maxAttempts: 3);

        await CrashAndReclaimAsync(id);
        await CrashAndReclaimAsync(id);
        await ClaimAsync(id, DateTime.UtcNow.AddMinutes(5));
        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var message = await LoadAsync(id);
        recipient.Calls.Should().Be(1);
        message.Status.Should().Be(EMessageStatus.DeadLettered, "two crashes and one failure are three attempts");
        message.DeadLetterReason.Should().Be(EDeadLetterReason.MaxAttempts);
    }
}
