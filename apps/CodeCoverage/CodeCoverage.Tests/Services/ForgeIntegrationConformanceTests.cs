using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// D17's contract, enforced. <see cref="IForgeIntegration.Capabilities"/> is a runtime promise with
/// no compiler behind it, so nothing but a test can keep it honest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both directions matter, and the second is the dangerous one.</b> An implementation that
/// advertises a capability it throws on is a visible bug the first time anyone uses it. One that
/// quietly <em>supports</em> something it does not advertise works perfectly — right up until a
/// caller starts trusting the array to decide what to skip, and silently skips a forge that could
/// have answered.
/// </para>
/// <para>
/// <b>Implementations are discovered, not listed</b>, so a fourth forge is covered the day its
/// assembly is referenced rather than when someone remembers to extend this file.
/// </para>
/// <para>
/// ⚠️ <b>The rules are also tested against deliberately non-conforming doubles.</b> With only one
/// implementation, which currently advertises every capability that has members, the
/// "unadvertised must throw" direction would otherwise pass <em>vacuously</em> — the loop would
/// have nothing to iterate. A conformance test that cannot fail is worse than no test, because it
/// reads like coverage.
/// </para>
/// </remarks>
public class ForgeIntegrationConformanceTests
{
    /// <summary>
    /// Which members each capability governs. A capability absent here has no members yet, and
    /// declaring it early is itself a violation.
    /// </summary>
    private static readonly Dictionary<EForgeCapability, Func<IForgeIntegration, Task>[]> Governs = new()
    {
        [EForgeCapability.Statuses] =
        [
            forge => forge.PublishStatusAsync(SampleRepository, "sha", "coverage",
                new ForgeVerdict(EForgeOutcome.Neutral, "t", "s"), null, CancellationToken.None),
        ],
        [EForgeCapability.Comments] =
        [
            forge => forge.PublishCommentAsync(SampleRepository, 1, "sha", "body", CancellationToken.None),
        ],
        [EForgeCapability.GitMirror] =
        [
            forge => forge.GetGitAccessAsync(SampleRepository, CancellationToken.None),
        ],
    };

    /// <summary>
    /// Members no capability governs, because every forge can answer them. Refusing one with
    /// <see cref="NotSupportedException"/> would turn an availability question into a capability
    /// one that callers have no array to filter on, so they must never refuse.
    /// </summary>
    private static readonly (string Name, Func<IForgeIntegration, Task> Invoke)[] Ungoverned =
    [
        (nameof(IForgeIntegration.GetTreeAsync), forge => forge.GetTreeAsync(SampleRepository, "main", CancellationToken.None)),
    ];

    /// <summary>
    /// Capabilities whose members live on a narrow interface of their own instead of on
    /// <see cref="IForgeIntegration"/> (merge-queue PRD M4). Declaring one means registering exactly one
    /// implementation of that interface for the same provider, and registering one means declaring it.
    /// </summary>
    private static readonly Dictionary<EForgeCapability, Type> SeparateSeams = new()
    {
        [EForgeCapability.MergeQueue] = typeof(IForgeMergeQueue),
    };

    /// <summary>Every member of <see cref="IForgeMergeQueue"/>; an implementation must refuse none of them.</summary>
    private static readonly (string Name, Func<IForgeMergeQueue, Task> Invoke)[] MergeQueueMembers =
    [
        (nameof(IForgeMergeQueue.GetActorRoleAsync), q => q.GetActorRoleAsync(SampleRepository, "octocat")),
        (nameof(IForgeMergeQueue.GetBranchRulesAsync), q => q.GetBranchRulesAsync(SampleRepository, "main")),
        (nameof(IForgeMergeQueue.GetBranchHeadAsync), q => q.GetBranchHeadAsync(SampleRepository, "main")),
        (nameof(IForgeMergeQueue.GetCheckStateAsync), q => q.GetCheckStateAsync(SampleRepository, SampleSha)),
        (nameof(IForgeMergeQueue.RerunCheckAsync), q => q.RerunCheckAsync(SampleRepository, new ForgeCheckRun(1, "build", "completed", "failure", 11, "github-actions", null))),
        (nameof(IForgeMergeQueue.GetCheckNamesOnCommitAsync), q => q.GetCheckNamesOnCommitAsync(SampleRepository, SampleSha)),
        (nameof(IForgeMergeQueue.DispatchWorkflowAsync), q => q.DispatchWorkflowAsync(SampleRepository, "regenerate.yml", "mq/main/pr-1", new Dictionary<string, string>())),
        (nameof(IForgeMergeQueue.GetWorkflowRunAsync), q => q.GetWorkflowRunAsync(SampleRepository, 1)),
        (nameof(IForgeMergeQueue.CancelWorkflowRunAsync), q => q.CancelWorkflowRunAsync(SampleRepository, 1)),
        (nameof(IForgeMergeQueue.ListWorkflowRunsAsync), q => q.ListWorkflowRunsAsync(SampleRepository, "regenerate.yml", "mq/main/pr-1")),
        (nameof(IForgeMergeQueue.DownloadArtifactAsync), q => q.DownloadArtifactAsync(SampleRepository, 1, "merge-queue-patch", 1024)),
        (nameof(IForgeMergeQueue.GetFileBlobShaAsync), q => q.GetFileBlobShaAsync(SampleRepository, "main", ".github/workflows/regenerate.yml")),
        (nameof(IForgeMergeQueue.GetWorkflowFilesAsync), q => q.GetWorkflowFilesAsync(SampleRepository, "main")),
        (nameof(IForgeMergeQueue.RequestMergeAsync), q => q.RequestMergeAsync(SampleRepository, 1, SampleSha, EForgeMergeMethod.Squash, null, null)),
        (nameof(IForgeMergeQueue.GetMergeRequestStatusAsync), q => q.GetMergeRequestStatusAsync(SampleRepository, 1, Guid.Empty.ToString())),
        (nameof(IForgeMergeQueue.GetReviewsAsync), q => q.GetReviewsAsync(SampleRepository, 1)),
        (nameof(IForgeMergeQueue.GetPullRequestCommitsAsync), q => q.GetPullRequestCommitsAsync(SampleRepository, 1)),
        (nameof(IForgeMergeQueue.PublishQueueCheckAsync), q => q.PublishQueueCheckAsync(SampleRepository, SampleSha, new ForgeQueueCheck(EForgeQueueCheckState.Queued, "t", "s", null), null)),
        (nameof(IForgeMergeQueue.AddLabelAsync), q => q.AddLabelAsync(SampleRepository, 1, "merge-queue")),
        (nameof(IForgeMergeQueue.RemoveLabelAsync), q => q.RemoveLabelAsync(SampleRepository, 1, "merge-queue")),
        (nameof(IForgeMergeQueue.PostCommentAsync), q => q.PostCommentAsync(SampleRepository, 1, "body")),
        (nameof(IForgeMergeQueue.GetWriterAccessAsync), q => q.GetWriterAccessAsync(SampleRepository, new ForgePullRequest(1, SampleSha, "feature", 1, "main", 1, "main", true), false)),
        (nameof(IForgeMergeQueue.RevokeWriterAccessAsync), q => q.RevokeWriterAccessAsync(SampleRepository, new ForgeWriterAccess("https://example.invalid/a/b.git", "https://example.invalid/a/b.git", "x-access-token", "t"))),
    ];

    private const string SampleSha = "4a5b6c7d8e9f00112233445566778899aabbccdd";

    private static Repository SampleRepository =>new() { Id = "Repositories/1", OwnerLogin = "acme", Name = "widget" };

    public static TheoryData<Type> Implementations
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var type in DiscoverImplementations()) data.Add(type);
            return data;
        }
    }

    private static IEnumerable<Type> DiscoverImplementations() => Discover(typeof(IForgeIntegration));

    private static IEnumerable<Type> Discover(Type seam)
        => typeof(GitHubForgeIntegration).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && seam.IsAssignableFrom(t))
            .OrderBy(t => t.Name);

    /// <summary>Which provider registers which separate seam, from the real implementations.</summary>
    private static IReadOnlyList<(Type Seam, EForgeProvider Provider)> DiscoverSeamProviders()
        => [.. SeparateSeams.Values.SelectMany(seam => Discover(seam).Select(type => (seam, ProviderOf(Create<object>(type)))))];

    private static EForgeProvider ProviderOf(object implementation)
        => (EForgeProvider)implementation.GetType().GetProperty(nameof(IForgeIntegration.Provider))!.GetValue(implementation)!;

    /// <summary>
    /// Guards the guard: if discovery ever returns nothing — a namespace move, a renamed assembly —
    /// the theories would vacuously pass and this contract would stop being enforced at all.
    /// </summary>
    [Fact]
    public void At_least_one_implementation_is_discovered()
        => DiscoverImplementations().Should().NotBeEmpty();

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task Every_implementation_conforms(Type type)
        => (await ViolationsAsync(Create(type), DiscoverSeamProviders())).Should().BeEmpty();

    /// <summary>
    /// The separate seam's own guard: at least one implementation exists, so the pairing rule below cannot
    /// pass vacuously, and none refuses a member — the capability is all of the interface or none of it.
    /// </summary>
    [Fact]
    public async Task Every_merge_queue_implementation_answers_every_member()
    {
        var implementations = Discover(typeof(IForgeMergeQueue)).ToList();
        implementations.Should().NotBeEmpty();

        var refused = new List<string>();
        foreach (var type in implementations)
        {
            var queue = Create<IForgeMergeQueue>(type);
            foreach (var (member, invoke) in MergeQueueMembers)
                if (await CaptureAsync(() => invoke(queue)) is NotSupportedException)
                    refused.Add($"{type.Name}.{member}");
        }
        refused.Should().BeEmpty();
    }

    [Fact]
    public void Each_forge_registers_at_most_one_merge_queue()
        => DiscoverSeamProviders().Should().OnlyHaveUniqueItems();

    /// <summary>The member list above must cover the interface, or a new member escapes the rule.</summary>
    [Fact]
    public void The_merge_queue_member_list_covers_the_interface()
        => MergeQueueMembers.Select(m => m.Name).Should().BeEquivalentTo(
            [.. typeof(IForgeMergeQueue).GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name).Distinct()]);

    /// <summary>
    /// Two implementations claiming one forge is the wiring bug <c>ForgeIntegrationResolver</c>
    /// throws on. Catching it here means finding out at build time rather than on the request that
    /// happens to resolve the wrong one.
    /// </summary>
    [Fact]
    public void Each_forge_is_claimed_by_exactly_one_implementation()
        => DiscoverImplementations().Select(t => Create(t).Provider).Should().OnlyHaveUniqueItems();

    // ── Proof that the rules bite ────────────────────────────────────────────────────────────────

    /// <summary>The dangerous direction: supports more than it admits to.</summary>
    [Fact]
    public async Task A_forge_that_supports_an_undeclared_capability_is_a_violation()
        => (await ViolationsAsync(new LyingForge())).Should()
            .ContainSingle().Which.Should().Contain("Comments").And.Contain("does not advertise");

    /// <summary>The visible direction: advertises more than it supports.</summary>
    [Fact]
    public async Task A_forge_that_refuses_a_declared_capability_is_a_violation()
        => (await ViolationsAsync(new RefusingForge())).Should()
            .ContainSingle().Which.Should().Contain("Statuses").And.Contain("advertises");

    /// <summary>
    /// Declaring a capability whose members do not exist yet would make callers filter for
    /// something they can never call. <c>Boards</c> and <c>CiIdentity</c> sit in the enum ahead of
    /// their members precisely so this rule has something to catch.
    /// </summary>
    [Fact]
    public async Task A_forge_declaring_a_capability_with_no_members_is_a_violation()
        => (await ViolationsAsync(new BoastfulForge())).Should()
            .ContainSingle().Which.Should().Contain("Boards").And.Contain("no members");

    /// <summary>A member outside every capability is not optional: refusing it is a violation.</summary>
    [Fact]
    public async Task A_forge_that_refuses_an_ungoverned_member_is_a_violation()
        => (await ViolationsAsync(new TreelessForge())).Should()
            .ContainSingle().Which.Should().Contain("GetTreeAsync").And.Contain("no capability governs");

    /// <summary>A capability on a separate seam, declared with nothing behind it.</summary>
    [Fact]
    public async Task A_forge_declaring_the_merge_queue_without_an_implementation_is_a_violation()
        => (await ViolationsAsync(new QueueClaimingForge(), seams: [])).Should()
            .ContainSingle().Which.Should().Contain("MergeQueue").And.Contain("registers no IForgeMergeQueue");

    /// <summary>The dangerous direction again: a merge queue the forge does not admit to.</summary>
    [Fact]
    public async Task A_merge_queue_for_a_forge_that_does_not_declare_it_is_a_violation()
        => (await ViolationsAsync(new BoastfulForge(), seams: [(typeof(IForgeMergeQueue), EForgeProvider.GitHub)])).Should()
            .Contain(v => v.Contains("MergeQueue") && v.Contains("does not advertise"));

    // ── The rules themselves ─────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<string>> ViolationsAsync(
        IForgeIntegration forge, IReadOnlyList<(Type Seam, EForgeProvider Provider)>? seams = null)
    {
        var name = forge.GetType().Name;
        var violations = new List<string>();
        seams ??= [];

        foreach (var capability in forge.Capabilities.Where(c => !Governs.ContainsKey(c) && !SeparateSeams.ContainsKey(c)))
            violations.Add($"{name} declares {capability}, which has no members — callers would filter for a call they cannot make.");

        foreach (var (capability, seam) in SeparateSeams)
        {
            var declared = forge.Capabilities.Contains(capability);
            var registered = seams.Count(s => s.Seam == seam && s.Provider == forge.Provider);
            if (declared && registered == 0)
                violations.Add($"{name} declares {capability} but its forge registers no {seam.Name}.");
            else if (!declared && registered > 0)
                violations.Add($"{name} does not advertise {capability} but its forge registers a {seam.Name} — a caller filtering on Capabilities would skip it.");
        }

        foreach (var (capability, members) in Governs)
        {
            var declared = forge.Capabilities.Contains(capability);
            foreach (var invoke in members)
            {
                var refused = await CaptureAsync(invoke, forge) is NotSupportedException;

                if (declared && refused)
                    violations.Add($"{name} advertises {capability} but refuses the call.");
                else if (!declared && !refused)
                    violations.Add($"{name} does not advertise {capability} but supports it — a caller filtering on Capabilities would skip a forge that could have answered.");
            }
        }

        foreach (var (member, invoke) in Ungoverned)
            if (await CaptureAsync(invoke, forge) is NotSupportedException)
                violations.Add($"{name} refuses {member}, which no capability governs — every forge must answer it.");

        return violations;
    }

    /// <summary>
    /// Builds an implementation with every constructor dependency substituted. The substitutes
    /// answer null, so a delegated call usually throws — which is fine, and is why only the
    /// exception's <em>type</em> is ever inspected. What is under test is whether a member refuses
    /// by contract, not whether it works.
    /// </summary>
    private static IForgeIntegration Create(Type type) => Create<IForgeIntegration>(type);

    private static T Create<T>(Type type) where T : class
    {
        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var arguments = constructor.GetParameters()
            .Select(p => p.ParameterType.IsInterface ? Substitute.For([p.ParameterType], null) : null)
            .ToArray();

        return (T)constructor.Invoke(arguments);
    }

    private static Task<Exception?> CaptureAsync(Func<IForgeIntegration, Task> invoke, IForgeIntegration forge)
        => CaptureAsync(() => invoke(forge));

    private static async Task<Exception?> CaptureAsync(Func<Task> invoke)
    {
        try
        {
            var task = invoke();
            if (task is not null) await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // ── Non-conforming doubles, each wrong in exactly one way ───────────────────────────────────

    private abstract class ConformanceDouble : IForgeIntegration
    {
        public EForgeProvider Provider => EForgeProvider.GitHub;
        public abstract EForgeCapability[] Capabilities { get; }

        public virtual Task<long> PublishStatusAsync(Repository repository, string sha, string name, ForgeVerdict verdict, long? existingId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public virtual Task PublishCommentAsync(Repository repository, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public virtual Task<ForgeGitAccess?> GetGitAccessAsync(Repository repository, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ForgeVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ForgeVisibility([], EForgeCredentialState.Ok));
        public Task<ForgeOwner[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default) => Task.FromResult<ForgeOwner[]>([]);
        public Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default) => Task.FromResult(ForgeAccess.Yes);
        public Task DeleteBranchAsync(Repository repository, string branch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default) => Task.FromResult<CommitComparison?>(null);
        public Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public virtual Task<ForgeTree?> GetTreeAsync(Repository repository, string branch, CancellationToken cancellationToken = default) => Task.FromResult<ForgeTree?>(null);
        public Task<ForgePullRequest?> GetPullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default) => Task.FromResult<ForgePullRequest?>(null);
        public Task ReconcileAsync(Account account, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Supports comments, admits only to statuses.</summary>
    private sealed class LyingForge : ConformanceDouble
    {
        public override EForgeCapability[] Capabilities => [EForgeCapability.Statuses];
        public override Task<long> PublishStatusAsync(Repository repository, string sha, string name, ForgeVerdict verdict, long? existingId, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public override Task PublishCommentAsync(Repository repository, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Advertises statuses, refuses them.</summary>
    private sealed class RefusingForge : ConformanceDouble
    {
        public override EForgeCapability[] Capabilities => [EForgeCapability.Statuses, EForgeCapability.Comments];
        public override Task PublishCommentAsync(Repository repository, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Conforms on every capability, but refuses to list a tree.</summary>
    private sealed class TreelessForge : ConformanceDouble
    {
        public override EForgeCapability[] Capabilities => [EForgeCapability.Statuses, EForgeCapability.Comments];
        public override Task<long> PublishStatusAsync(Repository repository, string sha, string name, ForgeVerdict verdict, long? existingId, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public override Task PublishCommentAsync(Repository repository, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task<ForgeTree?> GetTreeAsync(Repository repository, string branch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Declares the merge queue; conforms otherwise.</summary>
    private sealed class QueueClaimingForge : ConformanceDouble
    {
        public override EForgeCapability[] Capabilities => [EForgeCapability.MergeQueue];
    }

    /// <summary>Declares a capability that has no members yet.</summary>
    private sealed class BoastfulForge : ConformanceDouble
    {
        public override EForgeCapability[] Capabilities => [EForgeCapability.Boards];
    }
}
