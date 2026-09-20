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
    };

    private static Repository SampleRepository => new() { Id = "Repositories/1", OwnerLogin = "acme", Name = "widget" };

    public static TheoryData<Type> Implementations
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var type in DiscoverImplementations()) data.Add(type);
            return data;
        }
    }

    private static IEnumerable<Type> DiscoverImplementations()
        => typeof(GitHubForgeIntegration).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IForgeIntegration).IsAssignableFrom(t))
            .OrderBy(t => t.Name);

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
        => (await ViolationsAsync(Create(type))).Should().BeEmpty();

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

    // ── The rules themselves ─────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<string>> ViolationsAsync(IForgeIntegration forge)
    {
        var name = forge.GetType().Name;
        var violations = new List<string>();

        foreach (var capability in forge.Capabilities.Where(c => !Governs.ContainsKey(c)))
            violations.Add($"{name} declares {capability}, which has no members — callers would filter for a call they cannot make.");

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

        return violations;
    }

    /// <summary>
    /// Builds an implementation with every constructor dependency substituted. The substitutes
    /// answer null, so a delegated call usually throws — which is fine, and is why only the
    /// exception's <em>type</em> is ever inspected. What is under test is whether a member refuses
    /// by contract, not whether it works.
    /// </summary>
    private static IForgeIntegration Create(Type type)
    {
        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var arguments = constructor.GetParameters()
            .Select(p => p.ParameterType.IsInterface ? Substitute.For([p.ParameterType], null) : null)
            .ToArray();

        return (IForgeIntegration)constructor.Invoke(arguments);
    }

    private static async Task<Exception?> CaptureAsync(Func<IForgeIntegration, Task> invoke, IForgeIntegration forge)
    {
        try
        {
            var task = invoke(forge);
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

    /// <summary>Declares a capability that has no members yet.</summary>
    private sealed class BoastfulForge : ConformanceDouble
    {
        public override EForgeCapability[] Capabilities => [EForgeCapability.Boards];
    }
}
