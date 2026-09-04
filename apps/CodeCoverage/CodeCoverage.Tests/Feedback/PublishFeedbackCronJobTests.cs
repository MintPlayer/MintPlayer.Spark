using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The sweep is what makes a transient failure recoverable, and it had a gap:
/// it filtered only on <c>Build.FeedbackState</c>, so a comment that failed
/// AFTER the check-runs had succeeded — the common case, since the checks are
/// posted first — was stranded at <c>State: Retry</c> with nothing to
/// re-enqueue it. These tests cover the second query, including that its
/// generated index actually exists.
/// </summary>
public class PublishFeedbackCronJobTests : CoverageRavenTest
{
    private const long RepoId = 204431316;

    private sealed class RecordingBus : IMessageBus
    {
        public List<object> Broadcast { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            => Record(message);

        public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
            => Record(message);

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => Record(message);

            public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, string queueName, CancellationToken cancellationToken = default)
                => Record(message);

        private Task Record<TMessage>(TMessage message)
        {
            if (message is not null) Broadcast.Add(message);
            return Task.CompletedTask;
        }
    }

    private static PublishFeedbackCronJob Create(IAsyncDocumentSession session, RecordingBus bus)
        => new(session, bus, NullLogger<PublishFeedbackCronJob>.Instance);

    private static async Task SeedComment(IDocumentStore store, int pr, string state, DateTime? next)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new PullRequestFeedback
        {
            Repository = Entities.Repository.DocumentId(RepoId),
            PullRequestNumber = pr,
            State = state,
            NextAttemptAtUtc = next,
            InstallationId = 555,
            PendingBody = "body",
        }, PullRequestFeedback.DocumentId(RepoId, pr));
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// Also the proof that <c>PullRequestFeedbacks_Overview</c> is created: an
    /// unregistered index would throw IndexDoesNotExistException here rather
    /// than return nothing, since the query names it explicitly.
    /// </summary>
    [Fact]
    public async Task A_due_comment_retry_is_re_enqueued()
    {
        using var store = GetDocumentStore();
        await SeedComment(store, 79, "Retry", DateTime.UtcNow.AddMinutes(-1));
        WaitForIndexing(store);

        var bus = new RecordingBus();
        using var session = store.OpenAsyncSession();
        await Create(session, bus).RunAsync(default);

        bus.Broadcast.OfType<PublishPullRequestCommentMessage>().Should().ContainSingle();
        bus.Broadcast.OfType<PublishPullRequestCommentMessage>().First().FeedbackId
            .Should().Be(PullRequestFeedback.DocumentId(RepoId, 79));
    }

    [Fact]
    public async Task A_retry_that_is_not_due_yet_is_left_alone()
    {
        using var store = GetDocumentStore();
        await SeedComment(store, 79, "Retry", DateTime.UtcNow.AddHours(1));
        WaitForIndexing(store);

        var bus = new RecordingBus();
        using var session = store.OpenAsyncSession();
        await Create(session, bus).RunAsync(default);

        bus.Broadcast.OfType<PublishPullRequestCommentMessage>().Should().BeEmpty();
    }

    /// <summary>
    /// Terminal states must never be swept, or an installation without
    /// `Pull requests: write` would be retried every five minutes forever.
    /// </summary>
    [Theory]
    [InlineData("Posted")]
    [InlineData("Failed")]
    [InlineData("Unavailable")]
    public async Task Terminal_states_are_never_swept(string state)
    {
        using var store = GetDocumentStore();
        await SeedComment(store, 79, state, DateTime.UtcNow.AddMinutes(-1));
        WaitForIndexing(store);

        var bus = new RecordingBus();
        using var session = store.OpenAsyncSession();
        await Create(session, bus).RunAsync(default);

        bus.Broadcast.OfType<PublishPullRequestCommentMessage>().Should().BeEmpty();
    }
}
