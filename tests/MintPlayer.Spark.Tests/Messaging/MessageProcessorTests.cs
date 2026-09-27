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
        => new(
            services,
            Store,
            Options.Create(new SparkMessagingOptions { RetentionDays = retentionDays }),
            NullLogger<MessageProcessor>.Instance);

    private static ServiceProvider Services(IMessageTypeAllowList allowList, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(allowList);
        services.AddScoped<MessageCheckpoint>();
        services.AddScoped<IMessageCheckpoint>(sp => sp.GetRequiredService<MessageCheckpoint>());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private async Task<string> SeedMessageAsync(
        string? messageType = null,
        string? payloadJson = null,
        string owner = Owner,
        params HandlerExecution[] handlers)
    {
        var message = new SparkMessage
        {
            QueueName = "processor-tests",
            MessageType = messageType ?? typeof(Ping).AssemblyQualifiedName!,
            PayloadJson = payloadJson ?? JsonConvert.SerializeObject(new Ping { Text = "hello" }),
            CreatedAtUtc = DateTime.UtcNow,
            MaxAttempts = 3,
            AttemptCount = 1,
            Status = EMessageStatus.Processing,
            OwnerId = owner,
            ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            WakeUp = true,
            Handlers = [.. handlers],
        };
        await SeedAsync(session => session.StoreAsync(message));
        return message.Id!;
    }

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
}
