using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The only path in the application that destroys coverage data.
/// <para>
/// Everything else in this feature was built so that losing access to a repository destroys
/// nothing — a transfer, an uninstall, even a deletion on GitHub only disconnects. That makes this
/// the one place where a mistake is unrecoverable, and it had no test: the sweep sits on an id
/// convention (everything under a repository descends from <c>Commits/{repoGitHubId}/{sha}</c>)
/// rather than on a query, so it is exactly the kind of code that keeps working until someone adds
/// a document type whose id does not follow the rule.
/// </para>
/// </summary>
public class DeleteRepositoryDataRecipientTests : CoverageRavenTest
{
    private const long RepoId = 4242;
    private const long OtherRepoId = 9999;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static DeleteRepositoryDataRecipient CreateRecipient(IAsyncDocumentSession session)
        => CreateRecipient(session, out _);

    /// <summary>
    /// <paramref name="bus"/> receives the continuation the recipient re-queues when a repository
    /// is too large to finish in one message. Tests that seed less than
    /// <c>MaxDeletesPerMessage</c> documents should find it empty — a continuation for a
    /// repository that is already fully deleted would loop forever.
    /// </summary>
    private static DeleteRepositoryDataRecipient CreateRecipient(IAsyncDocumentSession session, out RecordingMessageBus bus)
    {
        bus = new RecordingMessageBus();

        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(bus);
        services.AddScoped<DeleteRepositoryDataRecipient>();
        return services.BuildServiceProvider().GetRequiredService<DeleteRepositoryDataRecipient>();
    }

    /// <summary>Records broadcasts instead of queueing them, so a test can see the continuation.</summary>
    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object?> Broadcast { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            Broadcast.Add(message);
            return Task.CompletedTask;
        }

        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);
    }

    /// <summary>
    /// Seeds one repository with the full spread of document types beneath it, plus a second
    /// repository whose documents must survive — a prefix sweep that is one character too greedy
    /// would take neighbours with it, and only a neighbour can catch that.
    /// </summary>
    private static async Task SeedAsync(IAsyncDocumentSession session, RepositoryConnection connection)
    {
        foreach (var (id, name) in new[] { (RepoId, "doomed"), (OtherRepoId, "innocent") })
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = id,
                Name = name,
                FullName = $"acme/{name}",
                OwnerLogin = "acme",
                Connection = id == RepoId ? connection : RepositoryConnection.Connected,
            }, Repository.DocumentId(id));

            var commitId = Commit.DocumentId(id, Sha);
            await session.StoreAsync(new Commit
            {
                Sha = Sha,
                Repository = Repository.DocumentId(id),
                FirstSeenAtUtc = DateTimeOffset.UtcNow,
            }, commitId);

            var buildId = Build.DocumentId(id, Sha, 7, 1);
            await session.StoreAsync(new Build { Commit = commitId, CiRunId = 7, CiRunAttempt = 1 }, buildId);
            await session.StoreAsync(new FileCoverage(), FileCoverage.DocumentId(buildId, "src/a.cs"));
            await session.StoreAsync(new BuildTreeSummary(), BuildTreeSummary.DocumentId(buildId));
            await session.StoreAsync(new CommitAssembly(), CommitAssembly.DocumentId(commitId));
            await session.StoreAsync(new PullRequestFeedback
            {
                Repository = Repository.DocumentId(id),
                PullRequestNumber = 3,
            }, PullRequestFeedback.DocumentId(id, 3));

            await session.StoreAsync(new ApiToken
            {
                Scope = "Repository",
                RepositoryGitHubId = id,
                AccountLogin = "acme",
                CreatedAtUtc = DateTime.UtcNow,
            }, ApiToken.DocumentId($"hash{id}"));
        }

        await session.SaveChangesAsync();
    }

    private static async Task<int> CountUnderAsync(IDocumentStore store, string prefix)
    {
        using var session = store.OpenAsyncSession();
        var n = 0;
        await using var stream = await session.Advanced.StreamAsync<object>(startsWith: prefix);
        while (await stream.MoveNextAsync()) n++;
        return n;
    }

    [Fact]
    public async Task Deleting_a_disconnected_repository_leaves_nothing_of_it_behind()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
            await SeedAsync(seed, RepositoryConnection.Disconnected);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            await CreateRecipient(session).HandleAsync(new DeleteRepositoryDataMessage
            {
                RepositoryGitHubId = RepoId,
                RequestedByUserId = "users/1",
            });
        }

        Assert.Equal(0, await CountUnderAsync(store, $"Commits/{RepoId}/"));
        Assert.Equal(0, await CountUnderAsync(store, $"PullRequestFeedbacks/{RepoId}/"));

        using var verify = store.OpenAsyncSession();
        Assert.Null(await verify.LoadAsync<Repository>(Repository.DocumentId(RepoId)));
        Assert.Null(await verify.LoadAsync<ApiToken>(ApiToken.DocumentId($"hash{RepoId}")));
    }

    /// <summary>
    /// A repository small enough to finish in one message must NOT re-queue itself.
    /// <para>
    /// The sweep hands the queue back by broadcasting the same message again when it hits its
    /// per-message budget, which is what stops a huge repository from blocking the PR comments it
    /// shares a queue with. Getting the budget check wrong in the other direction is worse than
    /// slow: a continuation queued after everything is already deleted would find the repository
    /// gone, re-queue, and spin forever on the publishing queue.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_repository_that_fits_in_one_message_is_not_requeued()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
            await SeedAsync(seed, RepositoryConnection.Disconnected);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            var recipient = CreateRecipient(session, out var bus);
            await recipient.HandleAsync(new DeleteRepositoryDataMessage
            {
                RepositoryGitHubId = RepoId,
                RequestedByUserId = "users/1",
            });

            Assert.Empty(bus.Broadcast);
        }
    }

    [Fact]
    public async Task Another_repositorys_documents_are_untouched()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
            await SeedAsync(seed, RepositoryConnection.Disconnected);
        WaitForIndexing(store);

        var before = await CountUnderAsync(store, $"Commits/{OtherRepoId}/");

        using (var session = store.OpenAsyncSession())
        {
            await CreateRecipient(session).HandleAsync(new DeleteRepositoryDataMessage
            {
                RepositoryGitHubId = RepoId,
                RequestedByUserId = "users/1",
            });
        }

        Assert.Equal(before, await CountUnderAsync(store, $"Commits/{OtherRepoId}/"));

        using var verify = store.OpenAsyncSession();
        Assert.NotNull(await verify.LoadAsync<Repository>(Repository.DocumentId(OtherRepoId)));
        Assert.NotNull(await verify.LoadAsync<ApiToken>(ApiToken.DocumentId($"hash{OtherRepoId}")));
    }

    /// <summary>
    /// The re-check that makes the queue safe. A delete is authorized against a disconnected
    /// repository, but it is applied later — and in between an upload or an `added` may have
    /// reconnected it. Deleting a live repository's history is not recoverable, so the recipient
    /// refuses rather than trusting the message.
    /// </summary>
    [Fact]
    public async Task A_repository_that_reconnected_before_the_message_was_applied_is_not_deleted()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
            await SeedAsync(seed, RepositoryConnection.Connected);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            await CreateRecipient(session).HandleAsync(new DeleteRepositoryDataMessage
            {
                RepositoryGitHubId = RepoId,
                RequestedByUserId = "users/1",
            });
        }

        using var verify = store.OpenAsyncSession();
        Assert.NotNull(await verify.LoadAsync<Repository>(Repository.DocumentId(RepoId)));
        Assert.True(await CountUnderAsync(store, $"Commits/{RepoId}/") > 0);
    }

    [Fact]
    public async Task Deleting_a_repository_that_is_already_gone_is_a_no_op()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session).HandleAsync(new DeleteRepositoryDataMessage
        {
            RepositoryGitHubId = 123456,
            RequestedByUserId = "users/1",
        });
    }
}
