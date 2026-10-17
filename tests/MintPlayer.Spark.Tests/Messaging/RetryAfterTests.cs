using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// R30 (dependency-updates PRD §13.13.3): <see cref="RetryAfterException"/> parks a handler for
/// <c>max(backoff, hint)</c> and counts a rate-limit park, not an attempt; parks are a weighted budget
/// (<c>AttemptCount + RateLimitParks / RateLimitParksPerAttempt &gt;= MaxAttempts</c>), and the dead-letter
/// reason says whether the parks made up most of it. Also: a shutdown cancellation is not a failure.
/// </summary>
public class RetryAfterTests : SparkTestDriver
{
    private const string Owner = "this-node/0001";
    private const string Queue = "retry-after-tests";

    public sealed class Call { public string? Text { get; set; } }

    /// <summary>Does whatever the test sets; one instance per test (registered as a singleton).</summary>
    public sealed class CallRecipient : IRecipient<Call>
    {
        public Func<CancellationToken, Task> OnHandle { get; set; } = _ => Task.CompletedTask;
        public int Calls;

        public Task HandleAsync(Call message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return OnHandle(cancellationToken);
        }
    }

    private sealed class AllowAll : IMessageTypeAllowList
    {
        public bool IsAllowedMessageType(string? assemblyQualifiedName) => true;
        public bool IsAllowedHandlerType(string? assemblyQualifiedName) => true;
    }

    private readonly CallRecipient recipient = new();

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageTypeAllowList>(new AllowAll());
        services.AddSingleton<IRecipient<Call>>(recipient);
        return services.BuildServiceProvider();
    }

    private MessageProcessor NewProcessor(IServiceProvider services, SparkMessagingOptions? options = null)
        => new(services, Store, Options.Create(options ?? new SparkMessagingOptions()), NullLogger<MessageProcessor>.Instance);

    private async Task<string> SeedAsync(int errors = 0, int parks = 0, int maxAttempts = 5, Action<SparkMessage>? shape = null)
    {
        var message = new SparkMessage
        {
            QueueName = Queue,
            MessageType = typeof(Call).AssemblyQualifiedName!,
            PayloadJson = JsonConvert.SerializeObject(new Call { Text = "hello" }),
            CreatedAtUtc = DateTime.UtcNow,
            MaxAttempts = maxAttempts,
            AttemptCount = 1,
            Status = EMessageStatus.Processing,
            OwnerId = Owner,
            ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            Handlers =
            [
                new HandlerExecution
                {
                    HandlerType = typeof(CallRecipient).AssemblyQualifiedName!,
                    Status = errors + parks > 0 ? EHandlerStatus.Failed : EHandlerStatus.Pending,
                    AttemptCount = errors,
                    RateLimitParks = parks,
                },
            ],
        };
        shape?.Invoke(message);
        await SeedAsync(session => session.StoreAsync(message));
        return message.Id!;
    }

    private async Task<SparkMessage> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<SparkMessage>(id);
    }

    private void ThrowRetryAfter(TimeSpan hint) => recipient.OnHandle = _ => throw new RetryAfterException(hint, "GitHub rate limit");
    private void ThrowError() => recipient.OnHandle = _ => throw new InvalidOperationException("boom");

    private async Task<SparkMessage> RunAsync(string id, SparkMessagingOptions? options = null)
    {
        using var services = Services();
        await NewProcessor(services, options).ProcessAsync(id, Owner, CancellationToken.None);
        return await LoadAsync(id);
    }

    // ---- the park ------------------------------------------------------------------------------

    [Fact]
    public async Task A_rate_limited_handler_is_parked_for_the_hint_when_it_is_longer_than_the_backoff()
    {
        ThrowRetryAfter(TimeSpan.FromMinutes(10));
        var before = DateTime.UtcNow;

        var message = await RunAsync(await SeedAsync());

        var handler = message.Handlers.Single();
        handler.Status.Should().Be(EHandlerStatus.Failed);
        handler.AttemptCount.Should().Be(0, "a park is not an attempt");
        handler.RateLimitParks.Should().Be(1);
        handler.LastError.Should().Be("GitHub rate limit");
        handler.RateLimitedUntilUtc!.Value.Should().BeOnOrAfter(before.AddMinutes(10));
        message.Status.Should().Be(EMessageStatus.Failed);
        message.NextAttemptAtUtc!.Value.Should().Be(handler.RateLimitedUntilUtc!.Value);
        message.RateLimitedUntilUtc!.Value.Should().Be(handler.RateLimitedUntilUtc!.Value, "the message shows how long it waits for the rate limit");
        message.OwnerId.Should().BeNull();
    }

    [Fact]
    public async Task A_rate_limited_handler_is_parked_for_its_backoff_when_the_hint_is_shorter()
    {
        // Two genuine failures already: the default table's entry for attempt 2 is 30 s.
        ThrowRetryAfter(TimeSpan.FromSeconds(1));
        var before = DateTime.UtcNow;

        var message = await RunAsync(await SeedAsync(errors: 2));

        var until = message.Handlers.Single().RateLimitedUntilUtc!.Value;
        until.Should().BeOnOrAfter(before.AddSeconds(30));
        until.Should().BeBefore(before.AddSeconds(60));
        message.NextAttemptAtUtc.Should().Be(until);
    }

    [Fact]
    public async Task A_RetryAfterException_is_recognised_thrown_synchronously_or_carried_as_an_inner_exception()
    {
        recipient.OnHandle = _ => throw new HttpRequestException("wrapper", new RetryAfterException(TimeSpan.FromMinutes(3)));
        var inner = await RunAsync(await SeedAsync());

        inner.Handlers.Single().RateLimitParks.Should().Be(1);
        inner.Handlers.Single().AttemptCount.Should().Be(0);
        new RetryAfterException(TimeSpan.FromSeconds(-5)).Delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_genuine_failure_after_a_park_clears_the_rate_limit_marker()
    {
        ThrowError();

        var message = await RunAsync(await SeedAsync(parks: 1, shape: m => m.Handlers[0].RateLimitedUntilUtc = DateTime.UtcNow.AddHours(1)));

        message.Handlers.Single().RateLimitedUntilUtc.Should().NotHaveValue();
        message.RateLimitedUntilUtc.Should().NotHaveValue();
        message.Handlers.Single().AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task A_park_that_would_end_after_the_expiry_dead_letters_the_message_as_Expired()
    {
        ThrowRetryAfter(TimeSpan.FromHours(1));

        var message = await RunAsync(await SeedAsync(shape: m => m.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)));

        message.Status.Should().Be(EMessageStatus.DeadLettered);
        message.DeadLetterReason.Should().Be(EDeadLetterReason.Expired);
    }

    // ---- the weighted budget -------------------------------------------------------------------

    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(5, 0, true)]
    [InlineData(0, 24, false)]
    [InlineData(0, 25, true)]
    [InlineData(2, 15, true)]
    [InlineData(2, 14, false)]
    [InlineData(4, 5, true)]
    public void The_budget_is_errors_plus_parks_divided_by_parks_per_attempt(int errors, int parks, bool exhausted)
    {
        var handler = new HandlerExecution { AttemptCount = errors, RateLimitParks = parks };

        RetryBudget.IsExhausted(handler, reclaims: 0, maxAttempts: 5, parksPerAttempt: 5).Should().Be(exhausted);
    }

    [Theory]
    // (errors before, parks before, throw a park?, dead-lettered?, reason)
    [InlineData(4, 3, true, false, null)]                                    // 4 errors + 4 parks: not dead
    [InlineData(4, 0, false, true, EDeadLetterReason.MaxAttempts)]           // 5 errors
    [InlineData(0, 23, true, false, null)]                                   // 24 parks: not dead
    [InlineData(0, 24, true, true, EDeadLetterReason.RateLimited)]           // 25 parks
    [InlineData(2, 14, true, true, EDeadLetterReason.RateLimited)]           // 2 errors + 15 parks
    [InlineData(1, 15, false, true, EDeadLetterReason.RateLimited)]          // 2 errors + 15 parks, ended by an error
    [InlineData(3, 5, false, true, EDeadLetterReason.MaxAttempts)]           // 4 errors + 5 parks: errors made up most
    public async Task The_processor_dead_letters_on_the_weighted_budget_with_the_reason_of_what_spent_it(
        int errorsBefore, int parksBefore, bool park, bool deadLettered, EDeadLetterReason? reason)
    {
        if (park) ThrowRetryAfter(TimeSpan.FromSeconds(1)); else ThrowError();

        var message = await RunAsync(await SeedAsync(errorsBefore, parksBefore));

        var handler = message.Handlers.Single();
        handler.AttemptCount.Should().Be(errorsBefore + (park ? 0 : 1));
        handler.RateLimitParks.Should().Be(parksBefore + (park ? 1 : 0));
        if (deadLettered)
        {
            handler.Status.Should().Be(EHandlerStatus.DeadLettered);
            message.Status.Should().Be(EMessageStatus.DeadLettered);
            message.DeadLetterReason.Should().Be(reason);
            message.RateLimitedUntilUtc.Should().NotHaveValue();
        }
        else
        {
            handler.Status.Should().Be(EHandlerStatus.Failed);
            message.Status.Should().Be(EMessageStatus.Failed);
            message.DeadLetterReason.HasValue.Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_queue_can_make_parks_cheaper()
    {
        ThrowRetryAfter(TimeSpan.FromSeconds(1));
        var options = new SparkMessagingOptions { Queues = { [Queue] = new SparkQueueOptions { RateLimitParksPerAttempt = 10 } } };

        var message = await RunAsync(await SeedAsync(parks: 24), options);

        message.Status.Should().Be(EMessageStatus.Failed, "25 parks at 10 per attempt are 2 attempts of 5");
        options.RateLimitParksPerAttemptFor(Queue).Should().Be(10);
        options.RateLimitParksPerAttemptFor("other").Should().Be(SparkMessagingOptions.DefaultRateLimitParksPerAttempt);
    }

    [Fact]
    public void RateLimitParksPerAttempt_binds_globally_and_per_queue_and_configuration_beats_code()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:Messaging:RateLimitParksPerAttempt"] = "7",
            ["Spark:Messaging:Queues:dependency-updates:RateLimitParksPerAttempt"] = "12",
        }).Build();
        var services = new ServiceCollection();
        new MintPlayer.Spark.SparkBuilder(services, configuration).AddMessaging();
        services.Configure<SparkMessagingOptions>(o =>
            o.Queues.TryAdd("dependency-updates", new SparkQueueOptions { RateLimitParksPerAttempt = 3 }));

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<SparkMessagingOptions>>().Value;

        options.RateLimitParksPerAttemptFor("dependency-updates").Should().Be(12, "configuration beats the code-declared 3");
        options.RateLimitParksPerAttemptFor("anything-else").Should().Be(7);
        new SparkMessagingOptions().RateLimitParksPerAttempt.Should().Be(5);
        new SparkMessagingOptions { RateLimitParksPerAttempt = 0 }.RateLimitParksPerAttemptFor("q").Should().Be(1);
    }

    // ---- shutdown is not a failure -------------------------------------------------------------

    [Fact]
    public async Task A_handler_cancelled_by_shutdown_is_not_charged_an_attempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recipient.OnHandle = async ct => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var id = await SeedAsync();
        using var services = Services();
        using var stopping = new CancellationTokenSource();

        var run = NewProcessor(services).ProcessAsync(id, Owner, stopping.Token, stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var before = DateTime.UtcNow;
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var message = await LoadAsync(id);
        message.Handlers.Single().AttemptCount.Should().Be(0, "the host stopped; the handler did not fail");
        message.Handlers.Single().Status.Should().Be(EHandlerStatus.Pending);
        message.Status.Should().Be(EMessageStatus.Failed, "parked for whichever host takes over");
        message.OwnerId.Should().BeNull();
        message.NextAttemptAtUtc!.Value.Should().BeBefore(before.AddSeconds(5), "due at once, not after a backoff");
    }

    [Fact]
    public async Task A_handler_cancelled_by_its_timeout_is_charged_an_attempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recipient.OnHandle = async ct => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var id = await SeedAsync();
        using var services = Services();
        using var timeout = new CancellationTokenSource();

        // The timeout token alone: the stopping token never fires.
        var run = NewProcessor(services).ProcessAsync(id, Owner, timeout.Token, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await timeout.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var message = await LoadAsync(id);
        message.Handlers.Single().AttemptCount.Should().Be(1);
    }
}
