using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The stickiness contract: one comment per pull request, whatever happens.
/// Forty pushes must not produce forty comments, a human deleting the comment
/// must not produce a second one on the next push, and an installation without
/// `Pull requests: write` must not burn five retries per build.
/// </summary>
public class PullRequestCommentPublisherTests : CoverageRavenTest
{
    /// <summary>
    /// Hand-written, per this app's convention (see Services/GitHubAuthTestFakes)
    /// — no mocking framework. Records calls so the tests can assert on the
    /// sequence rather than only the end state.
    /// </summary>
    private sealed class FakeGateway : IPullRequestCommentGateway
    {
        public List<ExistingComment> Comments { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? FailNextWith { get; set; }
        public bool UpdateThrowsNotFound { get; set; }
        private long nextId = 1000;

        public Task<IReadOnlyList<ExistingComment>> ListAsync(Entities.Repository repository, long installationId, int pullRequestNumber, CancellationToken cancellationToken)
        {
            Calls.Add("list");
            return Task.FromResult<IReadOnlyList<ExistingComment>>([.. Comments]);
        }

        public Task<long> CreateAsync(Entities.Repository repository, long installationId, int pullRequestNumber, string body, CancellationToken cancellationToken)
        {
            Calls.Add("create");
            Throw();
            var comment = new ExistingComment(++nextId, body, AuthoredByApp: true);
            Comments.Add(comment);
            return Task.FromResult(comment.Id);
        }

        public Task UpdateAsync(Entities.Repository repository, long installationId, long commentId, string body, CancellationToken cancellationToken)
        {
            Calls.Add($"update:{commentId}");
            Throw();
            if (UpdateThrowsNotFound) throw new NotFoundException("gone", System.Net.HttpStatusCode.NotFound);
            var at = Comments.FindIndex(c => c.Id == commentId);
            if (at < 0) throw new NotFoundException("gone", System.Net.HttpStatusCode.NotFound);
            Comments[at] = Comments[at] with { Body = body };
            return Task.CompletedTask;
        }

        private void Throw()
        {
            if (FailNextWith is null) return;
            var ex = FailNextWith;
            FailNextWith = null;
            throw ex;
        }
    }

    private const long RepoId = 204431316;
    private const long InstallationId = 555;
    private const int Pr = 79;

    private static Entities.Repository Repo() => new()
    {
        Id = Entities.Repository.DocumentId(RepoId),
        GitHubId = RepoId,
        Name = "MintPlayer.Spark",
        FullName = "MintPlayer/MintPlayer.Spark",
        OwnerLogin = "MintPlayer",
    };

    private static PullRequestCommentPublisher Create(IAsyncDocumentSession session, FakeGateway gateway)
        => new(session, gateway, NullLogger<PullRequestCommentPublisher>.Instance);

    private static string Body(string text) => $"{PullRequestCommentRenderer.Marker}\n{text}";

    private static async Task<PullRequestFeedback?> Load(IDocumentStore store)
    {
        using var read = store.OpenAsyncSession();
        return await read.LoadAsync<PullRequestFeedback>(PullRequestFeedback.DocumentId(RepoId, Pr));
    }

    [Fact]
    public async Task First_publish_creates_one_comment_and_records_its_id()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway();

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);

        gateway.Comments.Should().ContainSingle();
        var feedback = await Load(store);
        feedback!.CommentId.Should().Be(gateway.Comments[0].Id);
        feedback.State.Should().Be("Posted");
        feedback.LastPublishedSha.Should().Be("sha1");
        // Nothing owed once it is posted.
        feedback.PendingBody.Should().BeNull();
    }

    /// <summary>
    /// The core of G6. A new head sha edits the same comment rather than adding
    /// one — and, measured, an edit produces no new subscriber notification.
    /// </summary>
    [Fact]
    public async Task Second_publish_on_a_new_sha_updates_the_same_comment()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway();

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);
        var firstId = gateway.Comments[0].Id;

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha2", Body("second"), default);

        gateway.Comments.Should().ContainSingle();
        gateway.Comments[0].Id.Should().Be(firstId);
        gateway.Comments[0].Body.Should().Contain("second");
        gateway.Calls.Should().Contain($"update:{firstId}");

        var feedback = await Load(store);
        feedback!.LastPublishedSha.Should().Be("sha2");
    }

    [Fact]
    public async Task An_unchanged_body_on_the_same_sha_makes_no_api_call()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway();

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("same"), default);
        var callsAfterFirst = gateway.Calls.Count;

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("same"), default);

        gateway.Calls.Count.Should().Be(callsAfterFirst);
    }

    /// <summary>
    /// A human deleted the comment. Without marker adoption the bot would post
    /// a second one here, and a third on the push after that.
    /// </summary>
    [Fact]
    public async Task A_deleted_comment_is_re_adopted_by_marker_rather_than_duplicated()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway();

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);
        var original = gateway.Comments[0].Id;

        // The stored id now 404s, but an equivalent marked comment is present —
        // e.g. posted by an earlier process whose save was lost.
        gateway.Comments.Clear();
        gateway.Comments.Add(new ExistingComment(4242, Body("stale"), AuthoredByApp: true));

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha2", Body("second"), default);

        gateway.Comments.Should().ContainSingle();
        gateway.Comments[0].Id.Should().Be(4242);
        gateway.Comments[0].Id.Should().NotBe(original);
        (await Load(store))!.CommentId.Should().Be(4242);
    }

    [Fact]
    public async Task With_the_stored_id_gone_and_nothing_to_adopt_exactly_one_comment_is_created()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway();

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);

        gateway.Comments.Clear();

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha2", Body("second"), default);

        gateway.Comments.Should().ContainSingle();
        gateway.Calls.Should().Contain("list");
    }

    /// <summary>
    /// A human quoting the bot's body carries the marker too. Editing their
    /// comment would be both wrong and rude.
    /// </summary>
    [Fact]
    public async Task A_humans_comment_carrying_the_marker_is_never_adopted()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway();
        gateway.Comments.Add(new ExistingComment(9001, Body("quoting the bot"), AuthoredByApp: false));

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("ours"), default);

        gateway.Comments.Should().HaveCount(2);
        gateway.Comments.Should().Contain(c => c.Id == 9001 && c.Body.Contains("quoting the bot"));
        (await Load(store))!.CommentId.Should().NotBe(9001);
    }

    /// <summary>
    /// H7. The installation has not accepted the raised permission; retrying
    /// cannot help, so this is terminal-but-recoverable rather than a failure
    /// that burns MaxAttempts on every build.
    /// </summary>
    [Fact]
    public async Task A_403_is_recorded_as_unavailable_and_never_retried()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway { FailNextWith = new ApiException("Resource not accessible by integration", System.Net.HttpStatusCode.Forbidden) };

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);

        var feedback = await Load(store);
        feedback!.State.Should().Be("Unavailable");
        feedback.Attempts.Should().Be(0);
        feedback.NextAttemptAtUtc.HasValue.Should().BeFalse();
        // Nothing owed: the sweep must not carry this forever.
        feedback.PendingBody.Should().BeNull();
    }

    [Fact]
    public async Task A_transient_failure_schedules_a_retry_and_keeps_the_body_to_re_send()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway { FailNextWith = new ApiException("boom", System.Net.HttpStatusCode.InternalServerError) };

        using (var session = store.OpenAsyncSession())
            await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);

        var feedback = await Load(store);
        feedback!.State.Should().Be("Retry");
        feedback.Attempts.Should().Be(1);
        feedback.NextAttemptAtUtc.HasValue.Should().BeTrue();
        // The retry re-sends exactly this, rather than re-deriving it over the API.
        feedback.PendingBody.Should().Contain("first");
        feedback.PendingSha.Should().Be("sha1");
        feedback.InstallationId.Should().Be(InstallationId);
    }

    [Fact]
    public async Task The_publisher_never_throws_so_a_comment_failure_cannot_undo_the_check_runs()
    {
        using var store = GetDocumentStore();
        var gateway = new FakeGateway { FailNextWith = new ApiException("boom", System.Net.HttpStatusCode.BadGateway) };

        using var session = store.OpenAsyncSession();
        // No assertion beyond "does not throw" — that IS the contract.
        await Create(session, gateway).PublishAsync(Repo(), InstallationId, Pr, "sha1", Body("first"), default);
    }
}
