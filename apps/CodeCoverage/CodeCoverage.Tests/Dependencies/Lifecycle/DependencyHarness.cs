using System.Security.Cryptography;
using System.Text;
using CodeCoverage.CommitGraph;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.CommitGraph;
using CodeCoverage.Tests.Dependencies.Policy;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Tests.Services;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>A scripted <see cref="IForgeDependencyUpdates"/>: facts in dictionaries, every write recorded.</summary>
internal sealed class FakeForgeDependencyUpdates : IForgeDependencyUpdates
{
    public EForgeProvider Provider => EForgeProvider.GitHub;

    /// <summary>Each run's artifact files by run id.</summary>
    public Dictionary<long, Dictionary<string, byte[]>> Artifacts { get; } = [];

    public List<(long RunId, string Name)> ArtifactReads { get; } = [];

    public Dictionary<int, ForgeBotPullRequest> PullRequests { get; } = [];
    public List<(string Head, string Base, string Title, string Body, bool Draft)> Created { get; } = [];
    public List<(int Number, string Title, string Body)> Updated { get; } = [];
    public List<(long CommentId, EForgeReaction Reaction)> Reactions { get; } = [];
    public List<(string Sha, ForgeQueueCheck Check, long? ExistingId)> Checks { get; } = [];

    /// <summary><c>owner/repo@tag</c> → commit.</summary>
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    public List<string> TagReads { get; } = [];

    /// <summary><c>owner/repo@ref:path</c> → text.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    private int nextNumber = 41;
    private long nextCheck = 700;

    public Task<ForgeArtifactFiles> DownloadArtifactFilesAsync(Repository repository, long runId, string artifactName, IReadOnlyCollection<string> fileNames, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArtifactReads.Add((runId, artifactName));
        return Task.FromResult(Artifacts.TryGetValue(runId, out var files)
            ? new ForgeArtifactFiles(EForgeArtifactOutcome.Ok, files.Where(f => fileNames.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value), null)
            : ForgeArtifactFiles.Failed(EForgeArtifactOutcome.NotFound, "no artifact"));
    }

    public Task<ForgeBotPullRequest?> FindPullRequestAsync(Repository repository, string headBranch, string baseBranch, CancellationToken cancellationToken = default)
    {
        Fault("find");
        return Task.FromResult(PullRequests.Values.FirstOrDefault(p => p.HeadRef == headBranch && p.BaseRef == baseBranch && p.IsOpen));
    }

    public Task<ForgeBotPullRequest?> GetPullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => Task.FromResult(PullRequests.TryGetValue(number, out var pull) ? pull : null);

    public Task<ForgeBotPullRequest> CreatePullRequestAsync(Repository repository, string headBranch, string baseBranch, string title, string body, bool draft,
        CancellationToken cancellationToken = default)
    {
        Fault("create");
        // GitHub's answer to a second pull request from the same head: 422 "A pull request already exists".
        if (PullRequests.Values.Any(p => p.HeadRef == headBranch && p.BaseRef == baseBranch && p.IsOpen))
            throw new ForgeRequestException($"GitHub answered 422 to POST pulls: A pull request already exists for acme:{headBranch}.", 422);
        Created.Add((headBranch, baseBranch, title, body, draft));
        var pull = new ForgeBotPullRequest(nextNumber++, headBranch, HeadShaFor?.Invoke(headBranch) ?? "f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0", baseBranch, true, false, draft, null);
        PullRequests[pull.Number] = pull;
        Fault("create!");
        return Task.FromResult(pull);
    }

    public Task UpdatePullRequestAsync(Repository repository, int number, string title, string body, CancellationToken cancellationToken = default)
    {
        Updated.Add((number, title, body));
        return Task.CompletedTask;
    }

    public List<int> Closed { get; } = [];

    /// <summary>The head a created pull request reports, by branch (the branch head the writer pushed); a fixed sha when unset.</summary>
    public Func<string, string?>? HeadShaFor { get; set; }

    public Task ClosePullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        Closed.Add(number);
        if (PullRequests.TryGetValue(number, out var pull))
            PullRequests[number] = pull with { IsOpen = false };
        return Task.CompletedTask;
    }

    /// <summary>
    /// P15: throw <c>Fault</c> on the <c>Nth</c> (1-based) call named <c>Call</c> ("find", "create"); "create!" creates the
    /// pull request first and then throws, as an answer lost on the way back does. Each fault fires once.
    /// </summary>
    public List<(string Call, int Nth, Exception Fault)> Faults { get; } = [];

    private readonly Dictionary<string, int> calls = new(StringComparer.Ordinal);

    private void Fault(string call)
    {
        calls[call] = calls.GetValueOrDefault(call) + 1;
        var index = Faults.FindIndex(f => f.Call == call && f.Nth == calls[call]);
        if (index < 0) return;
        var fault = Faults[index].Fault;
        Faults.RemoveAt(index);
        throw fault;
    }

    public Task AddCommentReactionAsync(Repository repository, long commentId, EForgeReaction reaction, CancellationToken cancellationToken = default)
    {
        Reactions.Add((commentId, reaction));
        return Task.CompletedTask;
    }

    public Task<long> PublishCheckAsync(Repository repository, string sha, ForgeQueueCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        Checks.Add((sha, check, existingId));
        return Task.FromResult(existingId ?? nextCheck++);
    }

    public Task<string?> ResolveTagCommitAsync(Repository repository, string ownerRepo, string tag, CancellationToken cancellationToken = default)
    {
        TagReads.Add($"{ownerRepo}@{tag}");
        return Task.FromResult(Tags.TryGetValue($"{ownerRepo}@{tag}", out var sha) ? sha : null);
    }

    public Task<string?> GetRepositoryFileAsync(Repository repository, string ownerRepo, string gitRef, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Files.TryGetValue($"{ownerRepo}@{gitRef}:{path}", out var text) ? text : null);

    public ForgeBotPullRequest LastPull => PullRequests.Values.OrderBy(p => p.Number).Last();
}

/// <summary>
/// Drives <see cref="DependencyUpdateEngine"/> the way the messaging host does, like <see cref="MergeQueueHarness"/>: one fresh
/// session per handled message, the outbox's steps delivered in order, delayed steps held back until a test releases them,
/// and a clock the test moves (<see cref="MutableTime"/>).
/// </summary>
internal sealed class DependencyHarness
{
    public const long RepoGitHubId = 910040;
    public const long AccountGitHubId = 77;
    public const string BaseSha = DependencyTestKit.Sha;
    public const string NewBase = "2222222222222222222222222222222222222222";

    public const long LookupRunId = 5001, ApplyRunId = 5002, MigrateRunId = 5003;

    public static readonly string RepositoryId = Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);
    public static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, AccountGitHubId);

    /// <summary>coverage.yml on the default branch: the queue on, defaults for the updater, a migrate rule for nx.</summary>
    public const string Yml = """
        mergeQueue:
          enabled: true
          branches: [master]
        dependencies:
          autoMerge:
            maxCoverageDrop: "0.5"
          migrate:
            - match: "nx"
              onMajor: nx-migrate.yml
        """;

    public FakeForgeMergeQueue Queue { get; } = new();
    public FakeGitWriter Writer { get; } = new();
    public FakeOutbox Outbox { get; } = new();
    public ScriptedDiffService Forge { get; } = new() { Capabilities = [EForgeCapability.Statuses, EForgeCapability.Comments, EForgeCapability.MergeQueue] };
    public FakeForgeDependencyUpdates Updates { get; } = new();
    public MutableTime Time { get; } = new(DependencyTestKit.Now);
    public List<IForgeInstallationPermissions> Permissions { get; } = [];
    public List<DependencyUpdateStepMessage> Delayed { get; } = [];
    public List<object> Raised { get; } = [];

    public IDocumentStore Store { get; }

    public DependencyHarness(IDocumentStore store)
    {
        Store = store;
        SetYml(Yml);
        Queue.BranchHeads["master"] = BaseSha;
        Queue.Roles["maintainer"] = EForgeRepositoryRole.Write;
        Queue.Roles["visitor"] = EForgeRepositoryRole.Read;
        foreach (var (file, resource) in new[] { ("dependency-lookup.yml", "dependency-lookup.yml"), ("dependency-apply.yml", "dependency-apply.yml"), ("nx-migrate.yml", "nx-migrate.yml") })
        {
            var path = $".github/workflows/{file}";
            Queue.WorkflowFiles.TryAdd("master", []);
            Queue.WorkflowFiles["master"].Add(new ForgeWorkflowFile(path, DependencyOnboarding.ReadTemplate(resource)));
            foreach (var at in new[] { "master", BaseSha, NewBase, Writer.PlanCommitSha, Writer.StagedSha })
                Queue.Blobs[$"{at}:{path}"] = $"blob-{file}";
        }
    }

    public void SetYml(string yml)
    {
        foreach (var at in new[] { "master", BaseSha, NewBase })
            Forge.Files[$"{at}/{CoverageYml.FileName}"] = yml;
    }

    public async Task SeedAsync(bool enabled = true, int bisectBudget = 0)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Account
        {
            GitHubId = AccountGitHubId, Login = "acme", InstallationId = 4242, DependencyUpdates = enabled,
            DependencyBisectBudget = bisectBudget switch
            {
                1 => LookupReferences.EDependencyBisectBudget.One,
                4 => LookupReferences.EDependencyBisectBudget.Four,
                8 => LookupReferences.EDependencyBisectBudget.Eight,
                _ => LookupReferences.EDependencyBisectBudget.Off,
            },
        }, AccountId);
        await session.StoreAsync(new Entities.Repository
        {
            GitHubId = RepoGitHubId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
            DefaultBranch = "master", Account = AccountId, Provider = EForgeProvider.GitHub,
        }, RepositoryId);
        await session.SaveChangesAsync();
    }

    private DependencyUpdateEngine Engine(Raven.Client.Documents.Session.IAsyncDocumentSession session)
        => new(session, Forge, [Queue], [Updates], Permissions, Writer, Outbox, Time,
            Options.Create(new MergeQueueOptions { GitWriterUrl = "http://writer" }), new DependencyStateStore(session),
            new WorkflowHook(NullLogger<WorkflowHook>.Instance), NullLogger<DependencyUpdateEngine>.Instance);

    public async Task SignalAsync(DependencyUpdateSignalMessage signal)
    {
        using var session = Store.OpenAsyncSession();
        await Engine(session).HandleSignalAsync(signal);
    }

    public Task TriggerAsync(EDependencyRunTrigger trigger = EDependencyRunTrigger.Scheduled, IReadOnlyList<DependencySecurityFixRecord>? fixes = null)
        => SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Trigger, Trigger = trigger, SecurityFixes = fixes });

    public Task CommandAsync(EDependencyCommand command, string? argument, string actor = "maintainer", long commentId = 900)
        => SignalAsync(new DependencyUpdateSignalMessage
        {
            RepositoryId = RepositoryId, Kind = EDependencySignal.Command, Number = Updates.LastPull.Number, CommentId = commentId,
            Command = command, Argument = argument, Actor = actor,
        });

    public async Task StepAsync(DependencyUpdateStepMessage step)
    {
        using var session = Store.OpenAsyncSession();
        await Engine(session).HandleStepAsync(step);
    }

    /// <summary>Delivers every immediate step the outbox holds, in order, until none is left. Delayed ones are set aside.</summary>
    public async Task DrainAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (!Outbox.Messages.TryDequeue(out var queued)) return;
            if (queued.Message is not DependencyUpdateStepMessage step)
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
    public async Task ReleaseAsync(EDependencyStep kind)
    {
        await DrainAsync();
        var step = Delayed.LastOrDefault(s => s.Step == kind) ?? throw new InvalidOperationException($"No delayed {kind} step.");
        Delayed.Remove(step);
        await StepAsync(step);
        await DrainAsync();
    }

    public async Task<DependencyUpdateRun> RunAsync()
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(RepositoryId)))!;
    }

    public async Task<DependencyUpdateRunData> DataAsync()
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<DependencyUpdateRunData>(DependencyUpdateRunData.ForRepository(RepositoryId)))!;
    }

    public async Task<DependencyUpdateState> StateAsync()
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<DependencyUpdateState>(DependencyUpdateState.ForRepository(RepositoryId)))!;
    }

    public async Task<DependencyFleetState?> FleetAsync()
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<DependencyFleetState>(DependencyFleetState.ForAccount(AccountId));
    }

    public async Task StoreAsync(object document, string id)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(document, id);
        await session.SaveChangesAsync();
    }

    // ── Scripted runs ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The next dispatch starts run <paramref name="runId"/>, which completes on <paramref name="sha"/> with
    /// <paramref name="conclusion"/> (null: still running).</summary>
    public void ScriptRun(long runId, string sha, string? conclusion = "success", string branch = "master", bool dispatch = true)
    {
        if (dispatch)
            Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, runId, $"https://github.com/acme/widget/actions/runs/{runId}", null));
        Queue.Runs[runId] = new ForgeWorkflowRun(runId, conclusion is null ? "in_progress" : "completed", conclusion, sha, branch, $"https://github.com/acme/widget/actions/runs/{runId}");
    }

    public void ScriptLookup(LookupDocument lookup, long runId = LookupRunId, string sha = BaseSha)
    {
        ScriptRun(runId, sha);
        Updates.Artifacts[runId] = new() { ["lookup.json"] = Encoding.UTF8.GetBytes(DependencyContract.Serialize(lookup with { ExpectedSha = sha })) };
    }

    public const string Patch = "diff --git a/package.json b/package.json\n";

    /// <summary>The apply run: every planned group applied unless <paramref name="failed"/> names it.</summary>
    public void ScriptApply(long runId = ApplyRunId, string patch = Patch, params string[] failed)
    {
        ScriptRun(runId, Writer.PlanCommitSha, branch: DependencyUpdateRun.WorkBranch, dispatch: false);
        ApplyArtifact = (runId, patch, failed);
    }

    /// <summary>The apply run's artifact is written when the plan is known: <see cref="WriteApplyArtifactAsync"/>.</summary>
    public (long RunId, string Patch, string[] Failed)? ApplyArtifact { get; private set; }

    public static ApplyResult Result(DependencyPlan plan, string patch, IReadOnlyCollection<string> failed) => new()
    {
        PlanId = plan.PlanId,
        ExpectedSha = plan.ExpectedSha,
        Groups = [.. plan.Groups.Select(g => new ApplyGroupResult
        {
            Group = g.Group,
            Status = failed.Contains(g.Group) ? EApplyGroupStatus.Failed : EApplyGroupStatus.Applied,
            Items = [.. g.Items.Select(i => new ApplyItemResult { DepName = i.DepName, PackageFile = i.PackageFile, NewVersion = i.NewVersion, Adapter = "npm" })],
            Files = failed.Contains(g.Group) ? [] : [.. g.Items.Select(i => i.PackageFile).Distinct()],
            Message = failed.Contains(g.Group) ? "npm: no adapter for this lockfile" : null,
        })],
        Consistency = new ApplyConsistency { Status = EConsistencyStatus.Passed },
        Patch = new ArtifactPatch { File = "dependency.patch", Bytes = Encoding.UTF8.GetByteCount(patch), Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patch))) },
    };

    /// <summary>Puts the scripted apply artifact in place from the plan the saga stored.</summary>
    public async Task WriteApplyArtifactAsync()
    {
        if (ApplyArtifact is not { } apply) return;
        var data = await DataAsync();
        var plan = DependencyContract.Deserialize<DependencyPlan>(data.PlanJson!)!;
        Updates.Artifacts[apply.RunId] = new()
        {
            ["result.json"] = Encoding.UTF8.GetBytes(DependencyContract.Serialize(Result(plan, apply.Patch, apply.Failed))),
            ["dependency.patch"] = Encoding.UTF8.GetBytes(apply.Patch),
        };
    }

    /// <summary>
    /// A scheduled run up to its apply dispatch: lookup scripted and completed, the plan committed. The apply run is then
    /// finished by <see cref="FinishApplyAsync"/>.
    /// </summary>
    public async Task RunToApplyAsync(LookupDocument lookup, EDependencyRunTrigger trigger = EDependencyRunTrigger.Scheduled)
    {
        ScriptLookup(lookup);
        if (ApplyArtifact is null)
            ScriptApply();
        Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, ApplyArtifact!.Value.RunId, $"https://github.com/acme/widget/actions/runs/{ApplyArtifact.Value.RunId}", null));
        await TriggerAsync(trigger);
        await DrainAsync();
        await ReleaseAsync(EDependencyStep.PollRun); // the lookup run's poll
    }

    /// <summary>Completes the apply run with its artifact and drains to the next wait.</summary>
    public async Task FinishApplyAsync()
    {
        await WriteApplyArtifactAsync();
        await ReleaseAsync(EDependencyStep.PollRun);
    }

    public static ForgeCheckState Green(string sha) => MergeQueueHarness.Green(sha);
    public static ForgeCheckState Red(string sha) => MergeQueueHarness.Red(sha);

    /// <summary>A lookup with the given npm records, each candidate released long enough ago.</summary>
    public static LookupDocument Lookup(params LookupRecord[] records) => DependencyTestKit.Lookup(records);

    public static LookupRecord Npm(string depName, string current, string next, EUpdateType type = EUpdateType.Minor, string? group = null)
        => DependencyTestKit.Record(depName, current, DependencyTestKit.Candidate(type, next, group: group));
}
