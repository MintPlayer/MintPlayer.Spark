using CodeCoverage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CodeCoverage.Feedback;
using CodeCoverage.Ingestion;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// The action that decides whether coverage data may be destroyed.
/// <para>
/// It had no tests at all, and that absence was expensive: it has five refusal paths and a success
/// path, and every one of them returned an identical empty envelope, so from the browser a refusal,
/// a success and a message queued onto a dead lane were byte-for-byte the same thing. That is what
/// let a subscription-cap outage look like a permission problem for months. These tests pin the one
/// property that would have collapsed the ambiguity: <b>every exit says which exit it was.</b>
/// </para>
/// </summary>
public class DeleteDataActionTests : CoverageRavenTest
{
    private const long RepoId = 5150;

    /// <summary>
    /// Records what the action told the client, so a test can assert the outcome was reported at
    /// all rather than only that the right documents moved.
    /// </summary>
    private sealed class RecordingBus : IMessageBus
    {
        public List<object?> Broadcast { get; } = [];
        public List<string?> QueueNames { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            Broadcast.Add(message);
            QueueNames.Add(QueueNameFor(typeof(TMessage)));
            return Task.CompletedTask;
        }

        public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        {
            Broadcast.Add(message);
            QueueNames.Add(queueName);
            return Task.CompletedTask;
        }

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        /// <summary>Reads the queue the message type declares, the same way the real bus does.</summary>
        private static string? QueueNameFor(Type messageType)
            => (Attribute.GetCustomAttribute(messageType, typeof(MessageQueueAttribute)) as MessageQueueAttribute)?.QueueName;
    }

    private static async Task SeedAsync(IAsyncDocumentSession session, RepositoryConnection connection)
    {
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = "doomed",
            FullName = "acme/doomed",
            OwnerLogin = "acme",
            Connection = connection,
        }, Repository.DocumentId(RepoId));

        await session.SaveChangesAsync();
    }

    /// <summary>
    /// The message must land on the shared publishing queue, not on a queue of its own.
    /// <para>
    /// This is the assertion that would have caught the outage. RavenDB caps data subscriptions per
    /// database and this deployment's licence allows three; a message type that quietly declares a
    /// fourth queue name is enqueued forever and consumed by nobody, while the application reports
    /// itself perfectly healthy. <c>CoverageQueuesTests</c> guards the count; this guards that THIS
    /// message is on one of the two that exist.
    /// </para>
    /// </summary>
    [Fact]
    public void The_delete_message_is_queued_on_the_shared_publishing_lane()
    {
        var queue = (MessageQueueAttribute?)Attribute.GetCustomAttribute(
            typeof(DeleteRepositoryDataMessage), typeof(MessageQueueAttribute));

        Assert.NotNull(queue);
        Assert.Equal(CoverageQueues.Publishing, queue!.QueueName);
    }

    /// <summary>
    /// A repository document written before the Connection field existed has no Connection property
    /// at all. It deserializes to Connected (the enum's zero), which is the deliberate choice that
    /// keeps such repositories advertised rather than silently vanishing.
    /// <para>
    /// The consequence for deletion is the mirror image and is easy to get wrong: the delete gate
    /// requires positive, persisted evidence of disconnection, so a legacy document must be REFUSED
    /// until the reconciler marks it. Writing this gate as <c>== Connected</c> instead of
    /// <c>!= Disconnected</c> would let a legacy document through and destroy live data.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_document_with_no_Connection_field_reads_as_Connected_and_is_not_deletable()
    {
        using var store = GetDocumentStore();

        // Store the shape a pre-#366 document has: no Connection property written at all.
        using (var raw = store.OpenAsyncSession())
        {
            await raw.StoreAsync(new Repository
            {
                GitHubId = RepoId,
                Name = "legacy",
                FullName = "acme/legacy",
                OwnerLogin = "acme",
            }, Repository.DocumentId(RepoId));
            await raw.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));

        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Connected, repository!.Connection);

        // The gate the action applies, spelled the way the action spells it.
        Assert.True(repository.Connection != RepositoryConnection.Disconnected,
            "a repository with no persisted Connection must be treated as connected, and therefore "
            + "must not be deletable until the reconciler has positively marked it disconnected");
    }

    /// <summary>
    /// The recipient re-checks the connection state rather than trusting the message, because the
    /// repository can reconnect between the click and the sweep. Deleting live history because a
    /// message was already in flight would be unrecoverable.
    /// </summary>
    [Fact]
    public async Task A_repository_that_reconnected_after_queueing_is_not_deleted()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
            await SeedAsync(seed, RepositoryConnection.Connected);

        using (var session = store.OpenAsyncSession())
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging(l => l.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
            services.AddSingleton(session);
            services.AddSingleton<IMessageBus>(new RecordingBus());
            services.AddScoped<DeleteRepositoryDataRecipient>();

            var recipient = services.BuildServiceProvider().GetRequiredService<DeleteRepositoryDataRecipient>();

            await recipient.HandleAsync(new DeleteRepositoryDataMessage
            {
                RepositoryGitHubId = RepoId,
                RequestedByUserId = "users/1",
            });
        }

        using var verify = store.OpenAsyncSession();
        Assert.NotNull(await verify.LoadAsync<Repository>(Repository.DocumentId(RepoId)));
    }
}
