using System.Security.Claims;
using CodeCoverage.CustomActions;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// Every exit from <see cref="DeleteDataAction"/> must say which exit it was.
/// <para>
/// This is the property the whole investigation turned on. The action has five refusal paths and a
/// success path, and they all used to return an identical empty envelope — so a refusal, a success,
/// and a message queued onto a lane with no consumer were indistinguishable from the browser. A
/// dead RavenDB subscription therefore looked exactly like a permission problem, for months.
/// </para>
/// <para>
/// Asserting the notification is not decoration. It is the only thing that makes the difference
/// observable, and without a test it is one refactor away from being lost again.
/// </para>
/// </summary>
public class DeleteDataActionReportingTests : CoverageRavenTest
{
    private const long RepoId = 6100;
    private const string Owner = "acme";

    private sealed record Harness(
        DeleteDataAction Action,
        IClientAccessor Client,
        List<object?> Broadcast);

    /// <summary>Collects broadcasts so a test can assert nothing was queued on a refusal path.</summary>
    private sealed class RecordingBus : IMessageBus
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

    private static Harness CreateAction(IAsyncDocumentSession session, bool canManageOwner)
    {
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);

        var visibility = Substitute.For<ISparkVisibility>();
        visibility.CanManageOwnerAsync(Arg.Any<string>()).Returns(canManageOwner);

        var bus = new RecordingBus();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton(manager);
        services.AddSingleton(visibility);
        services.AddSingleton<IMessageBus>(bus);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) },
        });
        services.AddSingleton<IUserStore<SparkUser>>(Substitute.For<IUserStore<SparkUser>>());
        services.AddScoped<UserManager<SparkUser>>(sp => Substitute.ForPartsOf<UserManager<SparkUser>>(
            sp.GetRequiredService<IUserStore<SparkUser>>(), null!, null!, Array.Empty<IUserValidator<SparkUser>>(),
            Array.Empty<IPasswordValidator<SparkUser>>(), null!, null!, null!, null!));
        services.AddScoped<DeleteDataAction>();

        var action = services.BuildServiceProvider().GetRequiredService<DeleteDataAction>();
        return new Harness(action, client, bus.Broadcast);
    }

    private static async Task SeedAsync(IDocumentStore store, RepositoryConnection connection)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = "widget",
            FullName = $"{Owner}/widget",
            OwnerLogin = Owner,
            Connection = connection,
        }, Repository.DocumentId(RepoId));
        await session.SaveChangesAsync();
    }

    private static CustomActionArgs ArgsFor(string? repositoryId) => new()
    {
        Parent = repositoryId is null ? null : new PersistentObject
        {
            Id = repositoryId,
            Name = "Repository",
            ObjectTypeId = Guid.Empty,
            Attributes = [],
        },
    };

    /// <summary>
    /// No parent at all. Previously a silent return; now it says the repository could not be
    /// identified, which is a different sentence from "you may not do that" on purpose.
    /// </summary>
    [Fact]
    public async Task Without_a_parent_it_says_so_and_queues_nothing()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = CreateAction(session, canManageOwner: true);

        await harness.Action.ExecuteAsync(ArgsFor(null));

        harness.Client.Received().Notify(Arg.Any<string>(), NotificationKind.Error, Arg.Any<TimeSpan?>());
        Assert.Empty(harness.Broadcast);
    }

    [Fact]
    public async Task For_an_unknown_repository_it_says_already_deleted_and_queues_nothing()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = CreateAction(session, canManageOwner: true);

        await harness.Action.ExecuteAsync(ArgsFor(Repository.DocumentId(999999)));

        harness.Client.Received().Notify(Arg.Any<string>(), NotificationKind.Info, Arg.Any<TimeSpan?>());
        Assert.Empty(harness.Broadcast);
    }

    /// <summary>
    /// The refusal that used to be indistinguishable from success. It must name the owner, because
    /// "you do not manage acme" is actionable and an empty response is not.
    /// </summary>
    [Fact]
    public async Task For_an_owner_the_caller_does_not_manage_it_names_the_owner_and_queues_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, RepositoryConnection.Disconnected);

        using var session = store.OpenAsyncSession();
        var harness = CreateAction(session, canManageOwner: false);

        await harness.Action.ExecuteAsync(ArgsFor(Repository.DocumentId(RepoId)));

        harness.Client.Received().Notify(
            Arg.Is<string>(m => m.Contains(Owner)), NotificationKind.Error, Arg.Any<TimeSpan?>());
        Assert.Empty(harness.Broadcast);
    }

    /// <summary>
    /// A connected repository is never deletable here, and the message points at Resync — which is
    /// the actual next step for someone who expected it to be gone.
    /// </summary>
    [Fact]
    public async Task For_a_connected_repository_it_refuses_and_points_at_Resync()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, RepositoryConnection.Connected);

        using var session = store.OpenAsyncSession();
        var harness = CreateAction(session, canManageOwner: true);

        await harness.Action.ExecuteAsync(ArgsFor(Repository.DocumentId(RepoId)));

        harness.Client.Received().Notify(
            Arg.Is<string>(m => m.Contains("Resync")), NotificationKind.Warning, Arg.Any<TimeSpan?>());
        Assert.Empty(harness.Broadcast);
    }

    /// <summary>
    /// The success path. It queues exactly one message, says the work is in the background — the
    /// sweep is asynchronous, so "deleted" would be a lie — and navigates away, because the page
    /// the caller is on is about to describe an object that no longer exists.
    /// </summary>
    [Fact]
    public async Task On_success_it_queues_one_message_says_so_and_navigates_away()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, RepositoryConnection.Disconnected);

        using var session = store.OpenAsyncSession();
        var harness = CreateAction(session, canManageOwner: true);

        await harness.Action.ExecuteAsync(ArgsFor(Repository.DocumentId(RepoId)));

        var queued = Assert.Single(harness.Broadcast);
        var message = Assert.IsType<DeleteRepositoryDataMessage>(queued);
        Assert.Equal(RepoId, message.RepositoryGitHubId);

        harness.Client.Received().Notify(Arg.Any<string>(), NotificationKind.Success, Arg.Any<TimeSpan?>());
        harness.Client.Received().Navigate(Arg.Is<string>(r => r.Contains(Owner)));
    }
}
