using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// Drives <see cref="MergeQueueEngine"/> the way the messaging host does: one fresh session per handled message, the
/// outbox's steps delivered in order, delayed steps held back until a test releases them.
/// </summary>
internal sealed class MergeQueueHarness
{
    public const long RepoGitHubId = 910040;
    public const long AccountGitHubId = 77;
    public const string BaseSha = "1111111111111111111111111111111111111111";
    public const string OldBase = "0000000000000000000000000000000000000001";
    public const string NewBase = "2222222222222222222222222222222222222222";
    public const string Head = "3333333333333333333333333333333333333333";
    public const string Prepared = "4444444444444444444444444444444444444444";
    public const string Final = "5555555555555555555555555555555555555555";
    public const string MergeCommit = "feedfacefeedfacefeedfacefeedfacefeedface";
    public const int Number = 7;

    public static readonly string RepositoryId = Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);
    public static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, AccountGitHubId);

    public const string Yml = """
        mergeQueue:
          enabled: true
          branches: [master]
          enqueue:
            label: merge-queue
          postRebaseActionName: regenerate.yml
          postRebaseWhen: auto-resolved
          regeneratedPaths:
            - "**/App_Data/modelHashes.json"
          autoResolve:
            - path: "**/App_Data/modelHashes.json"
              strategy: take-base
          # The keep-both defaults (O35, O36) are on by default; these fixtures pin the take-base-only shape.
          keepBothDefaults: false
          checks:
            retryFlaky: 1
        """;

    public const string RegeneratePath = ".github/workflows/regenerate.yml";

    /// <summary>The setup guide's regenerate workflow in its essentials (§5.6): dispatch only, <c>contents: read</c>, no
    /// secret, a GitHub-hosted runner. The fork precondition passes it (§0 D65).</summary>
    public const string SafeRegenerate = """
        name: regenerate
        on:
          workflow_dispatch:
            inputs:
              expected_sha:
                required: true
        permissions:
          contents: read
        jobs:
          regenerate:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v7
                with:
                  ref: ${{ github.sha }}
                  persist-credentials: false
        """;

    private readonly IDocumentStore store;

    public IDocumentStore Store => store;

    public FakeForgeMergeQueue Queue { get; } = new();
    public FakeGitWriter Writer { get; } = new();
    public FakeOutbox Outbox { get; } = new();
    public ScriptedDiffService Forge { get; }
    public List<MergeQueueStepMessage> Delayed { get; } = [];
    public List<MergeQueueStepMessage> Handled { get; } = [];

    /// <summary>Every other message the saga put in its outbox, in order, as the drain passed it (e.g. §11's CheckReported).</summary>
    public List<object> Raised { get; } = [];

    /// <param name="mergeBase">What the forge's compare answers as the merge base of the base tip and the head: the
    /// base itself means the head already contains it (O20 case 1).</param>
    public MergeQueueHarness(IDocumentStore store, string mergeBase = OldBase)
    {
        this.store = store;
        Forge = new ScriptedDiffService(new CommitComparison(mergeBase, [], false))
        {
            // GitHub's own list (GitHubForgeIntegration.Capabilities): the queue, and buttons on its check (§0 O30).
            Capabilities = [EForgeCapability.Statuses, EForgeCapability.Comments, EForgeCapability.GitMirror, EForgeCapability.MergeQueue, EForgeCapability.CheckButtons],
        };
        Forge.Files[$"master/{CodeCoverage.Feedback.CoverageYml.FileName}"] = Yml;
        Forge.PullRequests[Number] = PullRequest();
        Queue.Roles["maintainer"] = EForgeRepositoryRole.Write;
        Queue.Roles["author"] = EForgeRepositoryRole.Write;
        Queue.BranchHeads["master"] = BaseSha;
        Queue.CheckNames[Head] = ["build"];
        Queue.CheckStates[Head] = Green(Head);
        Queue.CheckStates[Final] = Green(Final);
        Queue.Blobs["master:.github/workflows/regenerate.yml"] = "b10b";
        Queue.Blobs[$"{Prepared}:.github/workflows/regenerate.yml"] = "b10b";
        // §0 D65: what the fork precondition reads, safe unless a test says otherwise: the regenerate template only.
        Queue.WorkflowFiles["master"] = [new(RegeneratePath, SafeRegenerate)];
        Queue.WorkflowFiles[BaseSha] = [new(RegeneratePath, SafeRegenerate)];
        Forge.GitAccess[RepositoryId] = new ForgeGitAccess("https://github.com/acme/widget.git", "x-access-token", "test-token", "github:4242");
    }

    /// <summary>Delivers the next immediate step only.</summary>
    public async Task StepOnceAsync()
    {
        while (Outbox.Messages.TryDequeue(out var queued))
        {
            if (queued.Message is not MergeQueueStepMessage step)
            {
                Raised.Add(queued.Message);
                continue;
            }
            if (queued.Options?.Delay is not null)
            {
                Delayed.Add(step);
                continue;
            }
            await StepAsync(step);
            return;
        }
        throw new InvalidOperationException("No immediate step is queued.");
    }

    public static ForgePullRequest PullRequest(int number = Number, string head = Head, string author = "author", bool fork = false, bool maintainerCanModify = false)
        => new(number, head, $"feature-{number}", fork ? 555 : RepoGitHubId, "master", RepoGitHubId, "master", true)
        {
            AuthorLogin = author,
            Title = "Add widgets",
            HeadRepositoryFullName = fork ? "contributor/widget" : "acme/widget",
            MaintainerCanModify = maintainerCanModify,
        };

    public static ForgeCheckState Green(string sha)
        => new(sha, [new ForgeCheckRun(41, "build", "completed", "success", 11, "github-actions", null)], [], [new ForgeCheckSuite(11, "completed", "success", "github-actions", 1)]);

    public static ForgeCheckState Red(string sha)
        => new(sha, [new ForgeCheckRun(42, "build", "completed", "failure", 11, "github-actions", null)], [], [new ForgeCheckSuite(11, "completed", "failure", "github-actions", 1)]);

    public static ForgeCheckState Pending(string sha)
        => new(sha, [new ForgeCheckRun(43, "build", "in_progress", null, 11, "github-actions", null)], [], [new ForgeCheckSuite(11, "in_progress", null, "github-actions", 1)]);

    public async Task SeedAsync(EForgeProvider provider = EForgeProvider.GitHub)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = AccountGitHubId, Login = "acme", InstallationId = 4242 }, AccountId);
        await session.StoreAsync(new Entities.Repository
        {
            GitHubId = RepoGitHubId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
            DefaultBranch = "master", Account = AccountId, Provider = provider,
        }, RepositoryId);
        await session.SaveChangesAsync();
    }

    public List<IForgeInstallationPermissions> Permissions { get; } = [];

    /// <summary>When set, the saga talks to this writer instead of <see cref="Writer"/> (e.g. the real client over a stub).</summary>
    public IGitWriterSidecar? WriterOverride { get; set; }

    private MergeQueueEngine Engine(Raven.Client.Documents.Session.IAsyncDocumentSession session)
        => new(session, Forge, [Queue], Permissions, WriterOverride ?? Writer, Outbox, TimeProvider.System,
            Options.Create(new MergeQueueOptions { GitWriterUrl = "http://writer" }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Coverage:BaseUrl"] = "https://coverage.example" }).Build(),
            NullLogger<MergeQueueEngine>.Instance);

    public async Task SignalAsync(MergeQueueSignalMessage signal)
    {
        using var session = store.OpenAsyncSession();
        await Engine(session).HandleSignalAsync(signal);
    }

    public async Task StepAsync(MergeQueueStepMessage step)
    {
        Handled.Add(step);
        using var session = store.OpenAsyncSession();
        await Engine(session).HandleStepAsync(step);
    }

    public Task EnqueueAsync(string actor = "maintainer", EMergeQueueCommandSource source = EMergeQueueCommandSource.Label, string? label = "merge-queue", int number = Number)
        => SignalAsync(new MergeQueueSignalMessage
        {
            RepositoryId = RepositoryId, Kind = EMergeQueueSignal.Command, Number = number,
            Command = EMergeQueueCommand.Enqueue, Actor = actor, Source = source, Label = source == EMergeQueueCommandSource.Label ? label : null,
        });

    public Task DequeueAsync(string actor, int number = Number, EMergeQueueCommandSource source = EMergeQueueCommandSource.Comment)
        => SignalAsync(new MergeQueueSignalMessage
        {
            RepositoryId = RepositoryId, Kind = EMergeQueueSignal.Command, Number = number,
            Command = EMergeQueueCommand.Dequeue, Actor = actor, Source = source,
        });

    public Task SignalAsync(EMergeQueueSignal kind, int? number = null, string? sha = null, string? branch = null, long? runId = null)
        => SignalAsync(new MergeQueueSignalMessage { RepositoryId = RepositoryId, Kind = kind, Number = number, Sha = sha, Branch = branch, RunId = runId });

    /// <summary>Delivers every immediate step the outbox holds, in order, until none is left. Delayed ones are set aside.</summary>
    public async Task DrainAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (!Outbox.Messages.TryDequeue(out var queued)) return;
            if (queued.Message is not MergeQueueStepMessage step)
            {
                Raised.Add(queued.Message);
                continue;
            }
            if (queued.Options?.Delay is not null)
            {
                Delayed.Add(step);
                continue;
            }
            await StepAsync(step);
        }
        throw new InvalidOperationException("The saga did not settle within 100 steps.");
    }

    /// <summary>Delivers the most recently delayed step of <paramref name="kind"/>, then drains.</summary>
    public async Task ReleaseAsync(EMergeQueueStep kind)
    {
        await DrainAsync();
        var step = Delayed.LastOrDefault(s => s.Step == kind) ?? throw new InvalidOperationException($"No delayed {kind} step.");
        Delayed.Remove(step);
        await StepAsync(step);
        await DrainAsync();
    }

    public async Task<MergeQueueEntry> EntryAsync(int number = Number)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<MergeQueueEntry>(MergeQueueEntry.DocumentId(RepositoryId, number)))!;
    }

    public async Task<Entities.MergeQueue?> QueueDocumentAsync()
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<Entities.MergeQueue>(Entities.MergeQueue.DocumentId(RepositoryId));
    }
}
