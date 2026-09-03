using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The retry path. It re-sends the stored body verbatim rather than re-deriving
/// it, because re-deriving would re-resolve the base and re-fetch coverage.yml
/// over the GitHub API — and any drift between attempts would let the comment
/// contradict the check-runs it was rendered beside.
/// </summary>
public class PublishPullRequestCommentRecipientTests : CoverageRavenTest
{
    private const long RepoId = 204431316;
    private const int Pr = 79;

    private sealed record Published(string Body, string Sha, long InstallationId);

    private sealed class RecordingPublisher : IPullRequestCommentPublisher
    {
        public List<Published> Calls { get; } = [];

        public Task PublishAsync(Entities.Repository repository, long installationId, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken)
        {
            Calls.Add(new Published(body, sha, installationId));
            return Task.CompletedTask;
        }
    }

    private static PublishPullRequestCommentRecipient Create(IAsyncDocumentSession session, RecordingPublisher publisher)
        => new(session, publisher, NullLogger<PublishPullRequestCommentRecipient>.Instance);

    private static async Task Seed(IDocumentStore store, Action<PullRequestFeedback> configure, bool withRepository = true)
    {
        using var seed = store.OpenAsyncSession();

        if (withRepository)
        {
            await seed.StoreAsync(new Entities.Repository
            {
                GitHubId = RepoId,
                Name = "MintPlayer.Spark",
                FullName = "MintPlayer/MintPlayer.Spark",
                OwnerLogin = "MintPlayer",
            }, Entities.Repository.DocumentId(RepoId));
        }

        var feedback = new PullRequestFeedback
        {
            Repository = Entities.Repository.DocumentId(RepoId),
            PullRequestNumber = Pr,
            State = "Retry",
            Attempts = 1,
            InstallationId = 555,
            PendingBody = "the owed body",
            PendingSha = "sha1",
        };
        configure(feedback);

        await seed.StoreAsync(feedback, PullRequestFeedback.DocumentId(RepoId, Pr));
        await seed.SaveChangesAsync();
    }

    private static PublishPullRequestCommentMessage Message() => new()
    {
        FeedbackId = PullRequestFeedback.DocumentId(RepoId, Pr),
    };

    [Fact]
    public async Task The_owed_body_is_re_sent_verbatim()
    {
        using var store = GetDocumentStore();
        await Seed(store, _ => { });

        var publisher = new RecordingPublisher();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Calls.Should().ContainSingle();
        publisher.Calls[0].Body.Should().Be("the owed body");
        publisher.Calls[0].Sha.Should().Be("sha1");
        publisher.Calls[0].InstallationId.Should().Be(555);
    }

    /// <summary>
    /// A terminal state clears PendingBody, so a message that arrives late —
    /// the sweep raced a success, or the queue redelivered — must be a no-op
    /// rather than a second comment.
    /// </summary>
    [Fact]
    public async Task Nothing_owed_means_nothing_published()
    {
        using var store = GetDocumentStore();
        await Seed(store, f => { f.State = "Posted"; f.PendingBody = null; f.PendingSha = null; });

        var publisher = new RecordingPublisher();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The repository lost its installation between attempts — uninstalled, or
    /// suspended. There is nothing to publish through, and inventing one is not
    /// an option.
    /// </summary>
    [Fact]
    public async Task A_lost_installation_stops_the_retry()
    {
        using var store = GetDocumentStore();
        await Seed(store, f => f.InstallationId = null);

        var publisher = new RecordingPublisher();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_feedback_document_is_ignored_rather_than_throwing()
    {
        using var store = GetDocumentStore();
        var publisher = new RecordingPublisher();
        using var session = store.OpenAsyncSession();

        await Create(session, publisher).HandleAsync(new PublishPullRequestCommentMessage
        {
            FeedbackId = PullRequestFeedback.DocumentId(999999, 1),
        });

        publisher.Calls.Should().BeEmpty();
    }

    /// <summary>A deleted repository document must not take the queue down with it.</summary>
    [Fact]
    public async Task A_missing_repository_document_is_ignored_rather_than_throwing()
    {
        using var store = GetDocumentStore();
        await Seed(store, _ => { }, withRepository: false);

        var publisher = new RecordingPublisher();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// PendingSha is normally set, but a document written before it existed —
    /// or one whose pending write was partial — falls back to the last
    /// published sha rather than passing null down.
    /// </summary>
    [Fact]
    public async Task A_missing_pending_sha_falls_back_to_the_last_published_one()
    {
        using var store = GetDocumentStore();
        await Seed(store, f => { f.PendingSha = null; f.LastPublishedSha = "older-sha"; });

        var publisher = new RecordingPublisher();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Calls.Should().ContainSingle();
        publisher.Calls[0].Sha.Should().Be("older-sha");
    }
}
