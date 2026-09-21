using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The forge without a forge: answers from a script, or (default) with null — the exact shape a
/// repository with no API access produces, which is what most tests want so the resolver falls
/// through to the walk.
/// </summary>
/// <remarks>
/// <para>
/// Named for the diff it scripts rather than for the interface it implements, because that is what
/// every test using it cares about. It also stands in for file-content reads, which return null
/// unless scripted: a test that wants no <c>coverage.yml</c> gets exactly that, the overwhelmingly
/// common case.
/// </para>
/// <para>
/// <b>It is also its own resolver.</b> Production code selects an integration per repository
/// through <see cref="IForgeIntegrationResolver"/>; a test has exactly one forge and no interest in
/// selection, so implementing both lets a call site keep passing a single
/// <c>new ScriptedDiffService()</c> and get the same object back from every lookup. The selection
/// logic itself is tested against the real resolver, not here.
/// </para>
/// </remarks>
public sealed class ScriptedDiffService(CommitComparison? comparison = null)
    : IForgeIntegration, IForgeIntegrationResolver
{
    public List<(string BaseRef, string HeadSha)> Calls { get; } = [];

    /// <summary>Scripted git parents by sha; anything unlisted answers null (no API path).</summary>
    public Dictionary<string, string> Parents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scripted file content keyed by <c>{sha}/{path}</c>; anything unlisted answers null.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Owners the scripted viewer may manage. Empty unless a test says otherwise.</summary>
    public List<ForgeOwner> Owners { get; } = [];

    /// <summary>
    /// Whether the scripted forge claims a usable credential. Defaults to true so the feedback
    /// pipeline proceeds past its access check; set false to exercise the Unavailable branch.
    /// </summary>
    public bool AccessAvailable { get; set; } = true;

    /// <summary>Statuses published against this forge, in order, for assertions.</summary>
    public List<(string Sha, string Name, ForgeVerdict Verdict)> Statuses { get; } = [];

    /// <summary>Comments published against this forge, in order, for assertions.</summary>
    public List<(int PullRequestNumber, string Sha, string Body)> Comments { get; } = [];

    /// <summary>
    /// The forge this stands in for. Settable, and GitHub unless a test says otherwise: almost
    /// every test has one forge and does not care which, but a test about the boundary BETWEEN
    /// forges needs two of these and they must disagree.
    /// </summary>
    public EForgeProvider Provider { get; set; } = EForgeProvider.GitHub;

    public EForgeCapability[] Capabilities => [EForgeCapability.Statuses, EForgeCapability.Comments];

    // ── IForgeIntegrationResolver: one forge, so every lookup is the same object ─────────────────

    public IForgeIntegration? For(EForgeProvider provider) => provider == Provider ? this : null;

    public IForgeIntegration For(Repository repository) => this;

    public IReadOnlyList<IForgeIntegration> All => [this];

    public Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EForgeProvider>>([Provider]);

    // ── IForgeIntegration ────────────────────────────────────────────────────────────────────────

    /// <summary>The credential health this forge reports. Defaults to healthy.</summary>
    public EForgeCredentialState State { get; set; } = EForgeCredentialState.Ok;

    public Task<ForgeVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ForgeVisibility([.. Owners], State));

    public Task<ForgeOwner[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ForgeOwner[]>([.. Owners]);

    public Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default)
        => Task.FromResult(Owners.Any(o => o.Matches(owner)));

    public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default)
        => Task.FromResult(AccessAvailable
            ? ForgeAccess.Yes
            : ForgeAccess.No("No GitHub App installation for this repository."));

    public Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(Parents.TryGetValue(sha, out var parent) ? parent : null);

    public Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default)
    {
        Calls.Add((baseRef, headSha));
        return Task.FromResult(comparison);
    }

    public Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Files.TryGetValue($"{sha}/{path}", out var content) ? content : null);

    /// <summary>Branches this forge was asked to delete, in order.</summary>
    public List<string> DeletedBranches { get; } = [];

    public Task DeleteBranchAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
    {
        DeletedBranches.Add(branch);
        return Task.CompletedTask;
    }

    public Task<long> PublishStatusAsync(Repository repository, string sha, string name, ForgeVerdict verdict, long? existingId, CancellationToken cancellationToken = default)
    {
        Statuses.Add((sha, name, verdict));
        // Echo the id back on an update so a re-publish is distinguishable from a create.
        return Task.FromResult(existingId ?? Statuses.Count);
    }

    public Task PublishCommentAsync(Repository repository, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken = default)
    {
        Comments.Add((pullRequestNumber, sha, body));
        return Task.CompletedTask;
    }
}
