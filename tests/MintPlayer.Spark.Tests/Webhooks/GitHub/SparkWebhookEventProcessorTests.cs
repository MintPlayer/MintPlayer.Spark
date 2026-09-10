using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// Pins the entry-point logic of <see cref="SparkWebhookEventProcessor.ProcessWebhookAsync"/>:
/// signature validation drop, dev-app forwarding, and case-insensitive header normalization.
/// These three guard clauses run before Octokit's event-type dispatch — a regression here
/// either lets unsigned events through (security) or stops events from reaching message-bus
/// recipients (functional).
/// </summary>
public class SparkWebhookEventProcessorTests
{
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ISignatureService _signatureService = Substitute.For<ISignatureService>();
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly ILogger<SparkWebhookEventProcessor> _logger = NullLogger<SparkWebhookEventProcessor>.Instance;

    /// <summary>
    /// Manual fake — IDevWebSocketService is internal, and Castle DynamicProxy (used by
    /// NSubstitute) generates proxies in a different assembly that lacks internals access.
    /// </summary>
    private sealed class FakeDevWebSocketService : IDevWebSocketService
    {
        public List<(IDictionary<string, StringValues> Headers, string Body, GitHubWebhookRoutingContext Context)> Sent { get; } = [];
        public bool SendToClientsCalled => Sent.Count > 0;
        public Task SendToClients(IDictionary<string, StringValues> headers, string body, GitHubWebhookRoutingContext context)
        {
            Sent.Add((headers, body, context));
            return Task.CompletedTask;
        }
        public Task NewSocketClient(SocketClient client) => Task.CompletedTask;
    }

    /// <summary>
    /// Says which message types have a consumer. The processor asks before broadcasting, so a
    /// registry that says "yes" reproduces the old unconditional behaviour and one that says "no"
    /// exercises the suppression.
    /// </summary>
    private sealed class FakeRecipientRegistry(bool hasRecipient) : IMessageRecipientRegistry
    {
        public HashSet<Type> Consumed { get; } = [];
        public bool HasRecipient(Type? messageType)
        {
            if (messageType is not null) Consumed.Add(messageType);
            return hasRecipient;
        }
        public bool HasRecipient<TMessage>() => HasRecipient(typeof(TMessage));
    }

    private SparkWebhookEventProcessor CreateProcessor(
        GitHubWebhooksOptions? options = null,
        IServiceProvider? serviceProvider = null,
        IMessageRecipientRegistry? recipients = null)
    {
        options ??= new GitHubWebhooksOptions();
        serviceProvider ??= new ServiceCollection().BuildServiceProvider();
        return new SparkWebhookEventProcessor(
            _messageBus,
            recipients ?? new FakeRecipientRegistry(true),
            _signatureService,
            serviceProvider,
            _hostEnv,
            _logger,
            Options.Create(options));
    }

    /// <summary>
    /// A processor whose signature check passes, for the tests that are about what happens
    /// <em>after</em> the guard clauses. The substitute returns false by default, which is the right
    /// default for the guard tests and would silently make every one of these vacuous.
    /// </summary>
    private SparkWebhookEventProcessor ProcessorWithValidSignature(
        GitHubWebhooksOptions? options = null,
        IServiceProvider? serviceProvider = null,
        IMessageRecipientRegistry? recipients = null)
    {
        options ??= new GitHubWebhooksOptions { WebhookSecret = "secret" };
        _signatureService
            .VerifySignature(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        return CreateProcessor(options, serviceProvider, recipients);
    }

    private static Dictionary<string, StringValues> Headers(params (string Name, string Value)[] entries)
    {
        // GitHub sends headers case-sensitive — the processor must normalize, so tests deliberately
        // pass them in mixed case to exercise that path.
        var d = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var (n, v) in entries) d[n] = v;
        return d;
    }

    [Fact]
    public async Task Returns_early_when_signature_validation_fails()
    {
        var options = new GitHubWebhooksOptions { WebhookSecret = "secret" };
        _signatureService.VerifySignature(Arg.Any<string>(), "secret", "{}").Returns(false);
        var processor = CreateProcessor(options);

        await processor.ProcessWebhookAsync(
            Headers(("X-Hub-Signature-256", "sha256=bad"), ("X-GitHub-Event", "push")),
            "{}");

        await _messageBus.DidNotReceive().BroadcastAsync(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drops_event_when_no_WebhookSecret_is_configured()
    {
        // R2-C3: previously the processor skipped signature validation entirely
        // when WebhookSecret was empty, accepting any POST. Now it routes through
        // the (fail-closed) signature service unconditionally — empty secret →
        // verifier returns false → drop.
        var options = new GitHubWebhooksOptions { WebhookSecret = "" };
        _signatureService
            .VerifySignature(Arg.Any<string?>(), "", Arg.Any<string>())
            .Returns(false);
        var processor = CreateProcessor(options);

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Event", "push")),
            "{}");

        _signatureService.Received(1).VerifySignature(Arg.Any<string?>(), "", "{}");
        await _messageBus.DidNotReceive().BroadcastAsync(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Looks_up_signature_header_case_insensitively()
    {
        // GitHub spells it "X-Hub-Signature-256" but the dictionary the processor receives may
        // use a different case depending on the HTTP server. The processor must build a
        // case-insensitive view before reading the header. Returning false here keeps the
        // path short — we just need to prove the header value reached the signature service.
        var options = new GitHubWebhooksOptions { WebhookSecret = "secret" };
        _signatureService.VerifySignature("sha256=lower-case-key", "secret", "{}").Returns(false);
        var processor = CreateProcessor(options);

        // Lower-case header key — would miss the lookup if the dict were left case-sensitive.
        await processor.ProcessWebhookAsync(
            Headers(("x-hub-signature-256", "sha256=lower-case-key")),
            "{}");

        _signatureService.Received(1).VerifySignature("sha256=lower-case-key", "secret", "{}");
    }

    [Fact]
    public async Task Forwards_to_dev_socket_service_when_DevelopmentAppId_matches()
    {
        // R2-C3: signature now validates unconditionally (including for the
        // dev-forward path). Provide a valid signature so the forward branch
        // is reached.
        var options = new GitHubWebhooksOptions { DevelopmentAppId = 12345, WebhookSecret = "secret" };
        _signatureService
            .VerifySignature(Arg.Any<string?>(), "secret", Arg.Any<string>())
            .Returns(true);
        var devSocket = new FakeDevWebSocketService();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IDevWebSocketService>(devSocket)
            .BuildServiceProvider();
        _hostEnv.EnvironmentName.Returns("Development");
        var processor = CreateProcessor(options, serviceProvider);

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Hook-Installation-Target-ID", "12345"), ("X-GitHub-Event", "push")),
            """{"some":"payload"}""");

        devSocket.Sent.Should().ContainSingle();
        devSocket.Sent[0].Body.Should().Be("""{"some":"payload"}""");
        devSocket.Sent[0].Headers.Should().ContainKey("X-GitHub-Hook-Installation-Target-ID");

        // Forward path bypasses local processing, so no message bus broadcasts.
        await _messageBus.DidNotReceive().BroadcastAsync(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_silently_when_DevelopmentAppId_matches_but_no_dev_socket_registered()
    {
        // The forward path requires IDevWebSocketService to be in DI. Without it the processor
        // returns early WITHOUT processing locally — the operator chose dev forwarding.
        var options = new GitHubWebhooksOptions { DevelopmentAppId = 12345 };
        var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var processor = CreateProcessor(options, emptyProvider);

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Hook-Installation-Target-ID", "12345"), ("X-GitHub-Event", "push")),
            "{}");

        await _messageBus.DidNotReceive().BroadcastAsync(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_forward_when_DevelopmentAppId_does_not_match_target_id()
    {
        var options = new GitHubWebhooksOptions { DevelopmentAppId = 12345 };
        var devSocket = new FakeDevWebSocketService();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IDevWebSocketService>(devSocket)
            .BuildServiceProvider();
        var processor = CreateProcessor(options, serviceProvider);

        // Target-ID is the production app ID — the processor must NOT forward.
        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Hook-Installation-Target-ID", "99999"), ("X-GitHub-Event", "push")),
            "{}");

        devSocket.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_forward_when_DevelopmentAppId_is_not_configured()
    {
        var options = new GitHubWebhooksOptions { DevelopmentAppId = null };
        var devSocket = new FakeDevWebSocketService();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IDevWebSocketService>(devSocket)
            .BuildServiceProvider();
        var processor = CreateProcessor(options, serviceProvider);

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Hook-Installation-Target-ID", "12345"), ("X-GitHub-Event", "push")),
            "{}");

        devSocket.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Forwards_only_when_target_id_header_is_a_parseable_long()
    {
        // A non-numeric Target-ID can't equal the configured AppId, so the forward path is skipped.
        var options = new GitHubWebhooksOptions { DevelopmentAppId = 12345 };
        var devSocket = new FakeDevWebSocketService();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IDevWebSocketService>(devSocket)
            .BuildServiceProvider();
        var processor = CreateProcessor(options, serviceProvider);

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Hook-Installation-Target-ID", "not-a-number"), ("X-GitHub-Event", "push")),
            "{}");

        devSocket.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// The regression this whole area needed and never had. Dispatch used to be one override per
    /// event, over a base whose implementations are silent no-ops — so an event nobody had written
    /// an override for was discarded with no log, no error and no test that would notice.
    /// <c>installation_repositories</c> was one of them, for the entire life of the library, while
    /// an app sat waiting for it.
    /// </summary>
    [Theory]
    [InlineData("push")]
    [InlineData("installation")]
    [InlineData("installation_repositories")]
    [InlineData("installation_target")]
    [InlineData("organization")]
    [InlineData("repository")]
    [InlineData("member")]
    [InlineData("github_app_authorization")]
    [InlineData("an_event_that_does_not_exist_yet")]
    public async Task Every_event_produces_exactly_one_catch_all_message(string eventType)
    {
        var processor = ProcessorWithValidSignature();

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Event", eventType)),
            """{"action":"created"}""");

        await _messageBus.Received(1).BroadcastAsync(
            Arg.Is<GitHubWebhookMessage>(m => m.EventType == eventType),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_catch_all_carries_the_installation_and_repository_read_from_the_payload()
    {
        var processor = ProcessorWithValidSignature();

        // Read from the raw JSON, not off a deserialized event: most GitHub events have no Octokit
        // type here, and every consumer routes on these two fields.
        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Event", "member")),
            """{"installation":{"id":4711},"repository":{"full_name":"acme/widgets"}}""");

        await _messageBus.Received(1).BroadcastAsync(
            Arg.Is<GitHubWebhookMessage>(m => m.InstallationId == 4711 && m.RepositoryFullName == "acme/widgets"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_payload_that_cannot_be_parsed_is_still_delivered()
    {
        var processor = ProcessorWithValidSignature();

        // The signature already proved GitHub sent it, so an unparseable body is a shape we do not
        // know rather than an attack — route it with empty fields instead of losing it.
        await processor.ProcessWebhookAsync(Headers(("X-GitHub-Event", "push")), "not json at all");

        await _messageBus.Received(1).BroadcastAsync(
            Arg.Is<GitHubWebhookMessage>(m => m.InstallationId == 0 && m.RepositoryFullName == ""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_broadcast_when_no_recipient_consumes_it()
    {
        // A broadcast to a queue with no worker is not a no-op — it stores a document that nothing
        // will ever drain, one per delivery, forever.
        var registry = new FakeRecipientRegistry(hasRecipient: false);
        var processor = ProcessorWithValidSignature(recipients: registry);

        await processor.ProcessWebhookAsync(Headers(("X-GitHub-Event", "push")), """{"zen":"x"}""");

        await _messageBus.DidNotReceive().BroadcastAsync(
            Arg.Any<GitHubWebhookMessage>(), Arg.Any<CancellationToken>());
        registry.Consumed.Should().Contain(typeof(GitHubWebhookMessage));
    }

    [Fact]
    public async Task A_dev_forwarded_delivery_is_not_also_broadcast_locally()
    {
        var options = new GitHubWebhooksOptions { DevelopmentAppId = 12345, WebhookSecret = "secret" };
        var devSocket = new FakeDevWebSocketService();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IDevWebSocketService>(devSocket)
            .BuildServiceProvider();
        var processor = ProcessorWithValidSignature(options, serviceProvider);

        await processor.ProcessWebhookAsync(
            Headers(("X-GitHub-Hook-Installation-Target-ID", "12345"), ("X-GitHub-Event", "push")),
            """{"zen":"x"}""");

        devSocket.Sent.Should().HaveCount(1);
        await _messageBus.DidNotReceive().BroadcastAsync(
            Arg.Any<GitHubWebhookMessage>(), Arg.Any<CancellationToken>());
    }
}
