using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Newtonsoft.Json;
using Raven.Client;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// <see cref="MessageProcessor"/> called directly, one message at a time, so each refusal and each
/// handler outcome is pinned to the document state it leaves behind. The subscription-driven tests
/// only ever deliver well-formed messages to registered handlers, so every dead-letter path here was
/// untested — and they are the paths an attacker who can write into <c>SparkMessages</c> exercises.
/// </summary>
public class MessageProcessorTests : SparkTestDriver
{
    private const string Owner = "this-node/0001";

    public sealed class Ping { public string? Text { get; set; } }

    public sealed class PingRecipient : IRecipient<Ping>
    {
        public List<string?> Seen { get; } = [];

        public Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            Seen.Add(message.Text);
            return Task.CompletedTask;
        }
    }

    /// <summary>Resumes from a checkpoint when the handler execution carries one.</summary>
    public sealed class ResumingRecipient : ICheckpointRecipient<Ping>
    {
        public List<string> Resumed { get; } = [];
        public int FreshRuns;

        public Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            FreshRuns++;
            return Task.CompletedTask;
        }

        public Task HandleAsync(Ping message, string checkpoint, CancellationToken cancellationToken = default)
        {
            Resumed.Add(checkpoint);
            return Task.CompletedTask;
        }
    }

    /// <summary>Throws synchronously, so reflection wraps it in a TargetInvocationException.</summary>
    public sealed class SyncNonRetryableRecipient : IRecipient<Ping>
    {
        public Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
            => throw new NonRetryableException("sync refusal");
    }

    /// <summary>Throws from inside the task, so the exception arrives unwrapped.</summary>
    public sealed class AsyncNonRetryableRecipient : IRecipient<Ping>
    {
        public async Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("wrapper", new NonRetryableException("async refusal"));
        }
    }

    /// <summary>Allow-list stand-in, so a test can admit a type the real one would refuse.</summary>
    private sealed class AllowList(Func<string?, bool> messageTypes, Func<string?, bool> handlerTypes) : IMessageTypeAllowList
    {
        public bool IsAllowedMessageType(string? assemblyQualifiedName) => messageTypes(assemblyQualifiedName);
        public bool IsAllowedHandlerType(string? assemblyQualifiedName) => handlerTypes(assemblyQualifiedName);
    }

    private static readonly AllowList Everything = new(_ => true, _ => true);

    private MessageProcessor NewProcessor(IServiceProvider services, int retentionDays = 7)
        => NewProcessor(services, new SparkMessagingOptions { RetentionDays = retentionDays });

    private MessageProcessor NewProcessor(IServiceProvider services, SparkMessagingOptions options)
        => new(services, Store, Options.Create(options), NullLogger<MessageProcessor>.Instance);

    private static ServiceProvider Services(IMessageTypeAllowList allowList, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(allowList);
        services.AddScoped<MessageCheckpoint>();
        services.AddScoped<IMessageCheckpoint>(sp => sp.GetRequiredService<MessageCheckpoint>());
        services.AddScoped<MessageContext>();
        services.AddScoped<IMessageContext>(sp => sp.GetRequiredService<MessageContext>());
        services.AddScoped<MessageProgress>();
        services.AddScoped<IMessageProgress>(sp => sp.GetRequiredService<MessageProgress>());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private async Task<string> SeedMessageAsync(
        string? messageType = null,
        string? payloadJson = null,
        string owner = Owner,
        params HandlerExecution[] handlers)
        => await SeedMessageAsync(m =>
        {
            if (messageType is not null) m.MessageType = messageType;
            if (payloadJson is not null) m.PayloadJson = payloadJson;
            m.OwnerId = owner;
            m.Handlers = [.. handlers];
        });

    private async Task<string> SeedMessageAsync(Action<SparkMessage> shape)
    {
        var message = NewClaimedMessage();
        shape(message);
        await SeedAsync(session => session.StoreAsync(message));
        return message.Id!;
    }

    private static SparkMessage NewClaimedMessage()
        => new()
        {
            QueueName = "processor-tests",
            MessageType = typeof(Ping).AssemblyQualifiedName!,
            PayloadJson = JsonConvert.SerializeObject(new Ping { Text = "hello" }),
            CreatedAtUtc = DateTime.UtcNow,
            MaxAttempts = 3,
            AttemptCount = 1,
            Status = EMessageStatus.Processing,
            OwnerId = Owner,
            ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            WakeUp = true,
        };

    private async Task<(SparkMessage Message, bool HasExpiry)> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(id);
        var metadata = session.Advanced.GetMetadataFor(message);
        return (message, metadata.ContainsKey(Constants.Documents.Metadata.Expires));
    }

    private static HandlerExecution Handler<T>(string? checkpoint = null)
        => new() { HandlerType = typeof(T).AssemblyQualifiedName!, Status = EHandlerStatus.Pending, Checkpoint = checkpoint };

    [Fact]
    public async Task A_message_that_no_longer_exists_is_skipped()
    {
        using var services = Services(Everything);

        var act = () => NewProcessor(services).ProcessAsync("SparkMessages/gone", Owner, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_message_reclaimed_by_another_owner_is_left_alone()
    {
        var recipient = new PingRecipient();
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(recipient));
        var id = await SeedMessageAsync(owner: "other-node/0002");

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Processing, "the other owner is working on it");
        message.OwnerId.Should().Be("other-node/0002");
        message.Handlers.Should().BeEmpty();
        recipient.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_type_outside_the_allow_list_is_dead_lettered_without_being_resolved()
    {
        using var services = Services(new AllowList(_ => false, _ => true));
        var id = await SeedMessageAsync(messageType: "System.Diagnostics.Process, System.Diagnostics.Process");

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, hasExpiry) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.DeadLettered);
        message.OwnerId.Should().BeNull();
        message.ClaimExpiresAtUtc.Should().NotHaveValue();
        message.WakeUp.Should().BeFalse();
        hasExpiry.Should().BeTrue("a dead-lettered message is kept for RetentionDays, then expires");
    }

    [Fact]
    public async Task An_allowed_but_unresolvable_type_is_dead_lettered()
    {
        using var services = Services(Everything);
        var id = await SeedMessageAsync(messageType: "No.Such.Type, No.Such.Assembly");

        await NewProcessor(services, retentionDays: 0).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, hasExpiry) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.DeadLettered);
        hasExpiry.Should().BeFalse("RetentionDays 0 keeps dead letters for ever");
    }

    [Fact]
    public async Task A_null_payload_is_dead_lettered()
    {
        using var services = Services(Everything);
        var id = await SeedMessageAsync(payloadJson: "null");

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        (await LoadAsync(id)).Message.Status.Should().Be(EMessageStatus.DeadLettered);
    }

    [Fact]
    public async Task A_malformed_payload_parks_the_message_for_a_retry()
    {
        using var services = Services(Everything);
        var id = await SeedMessageAsync(payloadJson: "{ not json");

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Failed);
        message.OwnerId.Should().BeNull("a parked message is claimed by nobody, or the sweeper would miss it");
        message.NextAttemptAtUtc.Should().HaveValue();
        message.NextAttemptAtUtc!.Value.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task A_stored_handler_outside_the_allow_list_is_dead_lettered_individually()
    {
        var recipient = new PingRecipient();
        var allowed = typeof(PingRecipient).AssemblyQualifiedName;
        using var services = Services(
            new AllowList(_ => true, h => h == allowed),
            s => s.AddSingleton<IRecipient<Ping>>(recipient));
        var id = await SeedMessageAsync(handlers:
        [
            Handler<PingRecipient>(),
            new HandlerExecution { HandlerType = "Attacker.Gadget, Attacker", Status = EHandlerStatus.Pending },
        ]);

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        recipient.Seen.Should().Equal("hello");
        message.Handlers[0].Status.Should().Be(EHandlerStatus.Completed);
        message.Handlers[1].Status.Should().Be(EHandlerStatus.DeadLettered);
        message.Handlers[1].LastError.Should().StartWith("Handler type not in allow-list");
        message.Status.Should().Be(EMessageStatus.Completed, "one handler completed, so the message did");
    }

    [Fact]
    public async Task Unresolvable_and_unregistered_handlers_are_dead_lettered_and_so_is_the_message()
    {
        using var services = Services(Everything);
        var id = await SeedMessageAsync(handlers:
        [
            new HandlerExecution { HandlerType = "No.Such.Handler, No.Such.Assembly", Status = EHandlerStatus.Pending },
            // Resolvable, but nothing in DI provides it.
            Handler<PingRecipient>(),
        ]);

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Handlers[0].LastError.Should().StartWith("Cannot resolve handler type");
        message.Handlers[1].LastError.Should().Be($"Handler not found in DI: {nameof(PingRecipient)}");
        message.Handlers.Should().OnlyContain(h => h.Status == EHandlerStatus.DeadLettered);
        message.Status.Should().Be(EMessageStatus.DeadLettered, "every handler is dead-lettered");
    }

    [Fact]
    public async Task A_checkpointed_handler_resumes_from_its_checkpoint()
    {
        var recipient = new ResumingRecipient();
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(recipient));
        var id = await SeedMessageAsync(handlers: [Handler<ResumingRecipient>(checkpoint: "step-2")]);

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        recipient.Resumed.Should().Equal("step-2");
        recipient.FreshRuns.Should().Be(0, "a handler with a checkpoint must not start over");
        (await LoadAsync(id)).Message.Status.Should().Be(EMessageStatus.Completed);
    }

    [Fact]
    public async Task Non_retryable_exceptions_dead_letter_the_handler_whether_wrapped_or_not()
    {
        using var services = Services(Everything, s =>
        {
            s.AddScoped<IRecipient<Ping>, SyncNonRetryableRecipient>();
            s.AddScoped<IRecipient<Ping>, AsyncNonRetryableRecipient>();
        });
        var id = await SeedMessageAsync();

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Handlers.Should().HaveCount(2);
        message.Handlers.Should().OnlyContain(h => h.Status == EHandlerStatus.DeadLettered && h.AttemptCount == 0,
            "a non-retryable failure is final on the first attempt");
        message.Handlers.Select(h => h.LastError).Should().Equal("sync refusal", "wrapper");
        message.Status.Should().Be(EMessageStatus.DeadLettered);
    }

    [Fact]
    public async Task No_registered_recipients_completes_the_message()
    {
        using var services = Services(Everything);
        var id = await SeedMessageAsync();

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, hasExpiry) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Completed);
        message.CompletedAtUtc.Should().HaveValue();
        hasExpiry.Should().BeTrue();
    }

    // --- #460 M4: expiry, dead-letter reasons, throttling, scrubbing, context, progress ----------

    public sealed class FailingRecipient : IRecipient<Ping>
    {
        public int Calls;

        public Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("relay down");
        }
    }

    public sealed class ContextRecipient(IMessageContext context, List<string> seen) : IRecipient<Ping>
    {
        public Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            seen.Add($"{context.MessageId}|{context.QueueName}|{context.AttemptCount}|{context.ExpiresAtUtc:O}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Sends to three recipients; the first attempt dies after two of them.</summary>
    public sealed class FanOutRecipient(IMessageProgress progress, List<string> sent) : IRecipient<Ping>
    {
        public static int Attempts;

        public async Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            foreach (var recipient in new[] { "a", "b", "c" })
            {
                if (await progress.IsDoneAsync(recipient, cancellationToken))
                    continue;
                if (Attempts == 1 && recipient == "c")
                    throw new InvalidOperationException("crashed before c");

                sent.Add(recipient);
                await progress.MarkDoneAsync(recipient, cancellationToken);
            }
        }
    }

    /// <summary>Renews its own claim mid-handler, as ClaimedExecution's renewal loop does, and records the renewed expiry.</summary>
    public sealed class RenewingRecipient(Func<Task<DateTime?>> renew, List<DateTime?> renewed) : IRecipient<Ping>
    {
        public async Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
            => renewed.Add(await renew());
    }

    /// <summary>Runs after the first handler's step was saved and records the stored claim expiry.</summary>
    public sealed class ObservingRecipient(Func<Task<DateTime?>> read, List<DateTime?> observed) : IRecipient<Ping>
    {
        public async Task HandleAsync(Ping message, CancellationToken cancellationToken = default)
            => observed.Add(await read());
    }

    [Fact]
    public async Task A_handler_step_save_does_not_undo_a_claim_renewal_made_meanwhile()
    {
        var options = new SparkMessagingOptions { ClaimTtl = TimeSpan.FromMinutes(5) };
        var renewed = new List<DateTime?>();
        var observed = new List<DateTime?>();
        string id = null!;

        async Task<DateTime?> StoredExpiryAsync()
        {
            using var session = Store.OpenAsyncSession();
            return (await session.LoadAsync<SparkMessage>(id)).ClaimExpiresAtUtc;
        }

        using var services = Services(Everything, s =>
        {
            s.AddSingleton<IRecipient<Ping>>(new RenewingRecipient(async () =>
            {
                (await MessageClaims.TryRenewAsync(Store, id, Owner, options.ClaimTtl, CancellationToken.None))
                    .Should().BeTrue("the claim is ours");
                return await StoredExpiryAsync();
            }, renewed));
            s.AddSingleton<IRecipient<Ping>>(new ObservingRecipient(StoredExpiryAsync, observed));
        });

        // Loaded with an expiry well short of any renewal, so a write-back of the loaded value shows.
        id = await SeedMessageAsync(m =>
        {
            m.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);
            m.Handlers = [Handler<RenewingRecipient>(), Handler<ObservingRecipient>()];
        });

        await NewProcessor(services, options).ProcessAsync(id, Owner, CancellationToken.None);

        renewed.Should().ContainSingle().Which.Should().HaveValue();
        observed.Should().ContainSingle().Which.Should().HaveValue();
        observed[0]!.Value.Should().BeOnOrAfter(renewed[0]!.Value,
            "the first handler's step save must not write back the claim expiry the processor loaded");
        (await LoadAsync(id)).Message.Status.Should().Be(EMessageStatus.Completed);
    }

    private static SparkMessagingOptions WithQueue(SparkQueueOptions queue)
        => new() { Queues = { ["processor-tests"] = queue } };

    private async Task ReclaimAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(id);
        message.Status = EMessageStatus.Processing;
        message.OwnerId = Owner;
        message.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
        message.AttemptCount++;
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task A_message_past_its_expiry_is_dead_lettered_as_Expired_without_running()
    {
        var recipient = new PingRecipient();
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(recipient));
        var id = await SeedMessageAsync(m => m.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1));

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, hasExpiry) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.DeadLettered);
        message.DeadLetterReason.Should().Be(EDeadLetterReason.Expired);
        message.OwnerId.Should().BeNull();
        hasExpiry.Should().BeTrue();
        recipient.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Dead_letter_reasons_say_which_road_led_there()
    {
        using var failing = Services(Everything, s => s.AddScoped<IRecipient<Ping>, FailingRecipient>());
        var exhausted = await SeedMessageAsync(m => m.MaxAttempts = 1);
        await NewProcessor(failing).ProcessAsync(exhausted, Owner, CancellationToken.None);

        using var refusing = Services(new AllowList(_ => false, _ => true));
        var poison = await SeedMessageAsync(m => { });
        await NewProcessor(refusing).ProcessAsync(poison, Owner, CancellationToken.None);

        using var nonRetryable = Services(Everything, s => s.AddScoped<IRecipient<Ping>, SyncNonRetryableRecipient>());
        var refused = await SeedMessageAsync(m => { });
        await NewProcessor(nonRetryable).ProcessAsync(refused, Owner, CancellationToken.None);

        (await LoadAsync(exhausted)).Message.DeadLetterReason.Should().Be(EDeadLetterReason.MaxAttempts);
        (await LoadAsync(poison)).Message.DeadLetterReason.Should().Be(EDeadLetterReason.NonRetryable);
        (await LoadAsync(refused)).Message.DeadLetterReason.Should().Be(EDeadLetterReason.NonRetryable);
    }

    [Fact]
    public async Task A_completed_message_carries_no_dead_letter_reason()
    {
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(new PingRecipient()));
        var id = await SeedMessageAsync(m => { });

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Completed);
        message.DeadLetterReason.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_throttled_message_is_deferred_once_to_its_slot_and_admitted_when_it_returns()
    {
        var recipient = new PingRecipient();
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(recipient));
        var processor = NewProcessor(services, WithQueue(new SparkQueueOptions { MaxPerInterval = 1, Interval = TimeSpan.FromSeconds(1) }));
        var first = await SeedMessageAsync(m => { });
        var second = await SeedMessageAsync(m => { });

        await processor.ProcessAsync(first, Owner, CancellationToken.None);
        await processor.ProcessAsync(second, Owner, CancellationToken.None);

        (await LoadAsync(first)).Message.Status.Should().Be(EMessageStatus.Completed);
        var (deferred, _) = await LoadAsync(second);
        deferred.Status.Should().Be(EMessageStatus.Pending);
        deferred.OwnerId.Should().BeNull();
        deferred.ClaimExpiresAtUtc.Should().NotHaveValue();
        deferred.WakeUp.Should().BeFalse("it must stay invisible to the subscription until the sweeper wakes it at its slot");
        deferred.AttemptCount.Should().Be(0, "a deferral is not an attempt");
        deferred.Handlers.Should().BeEmpty("no handler started");
        deferred.NextAttemptAtUtc.Should().HaveValue();
        recipient.Seen.Should().HaveCount(1);
        processor.Admission.ReservationCount.Should().Be(1);

        var slot = deferred.NextAttemptAtUtc!.Value;
        await AsyncWait.UntilAsync(() => DateTime.UtcNow >= slot, "the reserved slot to arrive", TimeSpan.FromSeconds(10));
        await ReclaimAsync(second);
        await processor.ProcessAsync(second, Owner, CancellationToken.None);

        (await LoadAsync(second)).Message.Status.Should().Be(EMessageStatus.Completed);
        processor.Admission.ReservationCount.Should().Be(0, "the returning message used the slot it held");
        recipient.Seen.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_throttle_slot_after_the_expiry_dead_letters_the_message_as_Expired()
    {
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(new PingRecipient()));
        var processor = NewProcessor(services, WithQueue(new SparkQueueOptions { MaxPerInterval = 1, Interval = TimeSpan.FromHours(1) }));
        var first = await SeedMessageAsync(m => { });
        var resetMail = await SeedMessageAsync(m => m.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10));

        await processor.ProcessAsync(first, Owner, CancellationToken.None);
        await processor.ProcessAsync(resetMail, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(resetMail);
        message.Status.Should().Be(EMessageStatus.DeadLettered);
        message.DeadLetterReason.Should().Be(EDeadLetterReason.Expired);
        processor.Admission.ReservationCount.Should().Be(0);
    }

    [Fact]
    public async Task A_retry_that_would_run_after_the_expiry_dead_letters_the_message_as_Expired()
    {
        using var services = Services(Everything, s => s.AddScoped<IRecipient<Ping>, FailingRecipient>());
        // First backoff is 5 s by default; the message expires sooner.
        var id = await SeedMessageAsync(m => m.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(2));

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.DeadLettered);
        message.DeadLetterReason.Should().Be(EDeadLetterReason.Expired);
        message.Handlers.Should().OnlyContain(h => h.Status == EHandlerStatus.DeadLettered);
    }

    [Fact]
    public async Task A_queue_backoff_replaces_the_global_schedule_for_that_queue()
    {
        using var services = Services(Everything, s => s.AddScoped<IRecipient<Ping>, FailingRecipient>());
        var id = await SeedMessageAsync(m => { });

        await NewProcessor(services, WithQueue(new SparkQueueOptions { Backoff = [TimeSpan.FromMinutes(20)] }))
            .ProcessAsync(id, Owner, CancellationToken.None);

        var (message, _) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Failed);
        message.NextAttemptAtUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(19));
    }

    [Fact]
    public async Task A_scrubbed_message_loses_its_payload_once_terminal()
    {
        using var services = Services(Everything, s => s.AddSingleton<IRecipient<Ping>>(new PingRecipient()));
        var scrubbed = await SeedMessageAsync(m => m.ScrubPayloadOnTerminal = true);
        var kept = await SeedMessageAsync(m => { });

        await NewProcessor(services).ProcessAsync(scrubbed, Owner, CancellationToken.None);
        await NewProcessor(services).ProcessAsync(kept, Owner, CancellationToken.None);

        (await LoadAsync(scrubbed)).Message.PayloadJson.Should().BeEmpty();
        (await LoadAsync(kept)).Message.PayloadJson.Should().Contain("hello");
    }

    [Fact]
    public async Task A_handler_sees_the_current_message_through_IMessageContext()
    {
        var seen = new List<string>();
        using var services = Services(Everything,
            s => s.AddScoped<IRecipient<Ping>>(sp => new ContextRecipient(sp.GetRequiredService<IMessageContext>(), seen)));
        var expires = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = await SeedMessageAsync(m => m.ExpiresAtUtc = expires);

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        seen.Should().Equal($"{id}|processor-tests|1|{expires:O}");
    }

    [Fact]
    public async Task IMessageProgress_lets_a_retry_skip_the_steps_already_done_and_expires_with_its_message()
    {
        FanOutRecipient.Attempts = 0;
        var sent = new List<string>();
        using var services = Services(Everything,
            s => s.AddScoped<IRecipient<Ping>>(sp => new FanOutRecipient(sp.GetRequiredService<IMessageProgress>(), sent)));
        var id = await SeedMessageAsync(m => { });

        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);
        (await LoadAsync(id)).Message.Status.Should().Be(EMessageStatus.Failed);

        await ReclaimAsync(id);
        await NewProcessor(services).ProcessAsync(id, Owner, CancellationToken.None);

        sent.Should().Equal("a", "b", "c");
        var (message, _) = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Completed);
        message.Handlers[0].HasProgress.Should().BeTrue();

        using var session = Store.OpenAsyncSession();
        var progress = await session.LoadAsync<SparkMessageProgress>(SparkMessageProgress.IdFor(id, 0));
        progress.Steps.Should().Equal("a", "b", "c");
        progress.MessageId.Should().Be(id);
        var metadata = session.Advanced.GetMetadataFor(progress);
        metadata[Constants.Documents.Metadata.Collection].Should().Be("SparkMessageProgresses");
        metadata.ContainsKey(Constants.Documents.Metadata.Expires).Should().BeTrue("a sidecar expires with its message");
    }
}
