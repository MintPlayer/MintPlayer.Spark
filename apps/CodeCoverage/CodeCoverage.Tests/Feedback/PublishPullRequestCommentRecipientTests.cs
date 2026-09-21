using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using CodeCoverage.Tests.Services;

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

    /// <summary>
    /// The scripted forge stands in for the publisher now that this recipient goes through the
    /// forge seam. The old double also recorded the installation id it was handed; that assertion
    /// is gone because the recipient no longer passes one — resolving the credential is the
    /// implementation's business, which is the property the seam exists to enforce.
    /// </summary>
    private static ScriptedDiffService Forge(bool accessible = true)
        => new() { AccessAvailable = accessible };

    private static PublishPullRequestCommentRecipient Create(IAsyncDocumentSession session, ScriptedDiffService forge)
        => new(session, forge, NullLogger<PublishPullRequestCommentRecipient>.Instance);

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
            }, Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        }

        var feedback = new PullRequestFeedback
        {
            Repository = Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId),
            PullRequestNumber = Pr,
            State = "Retry",
            Attempts = 1,
            InstallationId = 555,
            PendingBody = "the owed body",
            PendingSha = "sha1",
        };
        configure(feedback);

        await seed.StoreAsync(feedback, PullRequestFeedback.DocumentId(EForgeProvider.GitHub, RepoId, Pr));
        await seed.SaveChangesAsync();
    }

    private static PublishPullRequestCommentMessage Message() => new()
    {
        FeedbackId = PullRequestFeedback.DocumentId(EForgeProvider.GitHub, RepoId, Pr),
    };

    [Fact]
    public async Task The_owed_body_is_re_sent_verbatim()
    {
        using var store = GetDocumentStore();
        await Seed(store, _ => { });

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Should().ContainSingle();
        publisher.Comments[0].Body.Should().Be("the owed body");
        publisher.Comments[0].Sha.Should().Be("sha1");
        // The installation id this used to assert is gone on purpose: the recipient no longer
        // passes a credential, because resolving one is the forge implementation's business.
        // Asserting it here would pin the coupling the seam exists to remove.
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

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Should().BeEmpty();
    }

    /// <summary>
    /// The repository lost its installation between attempts — uninstalled, or
    /// suspended. There is nothing to publish through, and inventing one is not
    /// an option.
    /// </summary>
    [Fact]
    public async Task Lost_forge_access_stops_the_retry()
    {
        using var store = GetDocumentStore();
        await Seed(store, f => f.InstallationId = null);

        // ⚠️ Renamed, and the gate moved. This used to pass because the recipient read the stored
        // InstallationId and bailed when it was null. That was a GitHub credential deciding whether
        // a comment could be retried, on a record that must outlive GitHub-only — and it meant a
        // repository whose id was never stamped could never retry at all. The recipient now asks
        // the forge, so the seeded null is irrelevant and the forge's answer is the gate.
        var publisher = Forge(accessible: false);
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_feedback_document_is_ignored_rather_than_throwing()
    {
        using var store = GetDocumentStore();
        var publisher = Forge();
        using var session = store.OpenAsyncSession();

        await Create(session, publisher).HandleAsync(new PublishPullRequestCommentMessage
        {
            FeedbackId = PullRequestFeedback.DocumentId(EForgeProvider.GitHub, 999999, 1),
        });

        publisher.Comments.Should().BeEmpty();
    }

    /// <summary>A deleted repository document must not take the queue down with it.</summary>
    [Fact]
    public async Task A_missing_repository_document_is_ignored_rather_than_throwing()
    {
        using var store = GetDocumentStore();
        await Seed(store, _ => { }, withRepository: false);

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Should().BeEmpty();
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

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Should().ContainSingle();
        publisher.Comments[0].Sha.Should().Be("older-sha");
    }
}
