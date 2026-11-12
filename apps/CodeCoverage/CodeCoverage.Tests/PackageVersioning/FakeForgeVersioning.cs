using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>An in-memory <see cref="IForgeVersioning"/>: one pull request, its issues, and every write recorded.</summary>
internal sealed class FakeForgeVersioning : IForgeVersioning
{
    public EForgeProvider Provider => EForgeProvider.GitHub;

    public ForgePullRequestText? PullRequest { get; set; }
    public Dictionary<int, string[]> Issues { get; } = [];
    public List<string> TitleUpdates { get; } = [];
    public List<ForgeVersioningCheck> Checks { get; } = [];
    public List<ForgeRelease> Releases { get; } = [];
    public List<(string Branch, string Sha)> Branches { get; } = [];
    public List<(string Head, string Base, string Title, string Body)> OpenedPullRequests { get; } = [];
    public List<(int Number, string Body)> Comments { get; } = [];
    /// <summary>The merged pull requests per commit, for <see cref="MergedPullRequestsAsync"/>.</summary>
    public Dictionary<string, int[]> MergedPullRequests { get; } = [];
    public List<(string Workflow, string Branch)> Dispatches { get; } = [];
    public Exception? FailTitleUpdateWith { get; set; }

    /// <summary>More pull requests by number, beside <see cref="PullRequest"/> (#570 M8c: a backport and its source).</summary>
    public Dictionary<int, ForgePullRequestText> OtherPullRequests { get; } = [];

    /// <summary>When set, reading a pull request throws this (a forge that is down).</summary>
    public Exception? ReadFailure { get; set; }

    public Task<ForgePullRequestText?> GetPullRequestTextAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => ReadFailure is { } failure
            ? Task.FromException<ForgePullRequestText?>(failure)
            : Task.FromResult(PullRequest?.Number == number ? PullRequest : OtherPullRequests.GetValueOrDefault(number));

    public Task<ForgeIssue?> GetIssueAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => Task.FromResult(Issues.TryGetValue(number, out var labels) ? new ForgeIssue(number, labels) : null);

    public Task UpdatePullRequestTitleAsync(Repository repository, int number, string title, CancellationToken cancellationToken = default)
    {
        if (FailTitleUpdateWith is { } failure) throw failure;
        TitleUpdates.Add(title);
        PullRequest = PullRequest! with { Title = title };
        return Task.CompletedTask;
    }

    public Task<long> PublishCheckAsync(Repository repository, string sha, ForgeVersioningCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        Checks.Add(check);
        return Task.FromResult((long)Checks.Count);
    }

    public Task<ForgeWriteOutcome> CreateReleaseAsync(Repository repository, ForgeRelease release, CancellationToken cancellationToken = default)
    {
        if (Releases.Any(r => r.TagName == release.TagName)) return Task.FromResult(ForgeWriteOutcome.Exists);
        Releases.Add(release);
        return Task.FromResult(ForgeWriteOutcome.Created);
    }

    /// <summary>Branches whose creation the forge refuses with this message (a ruleset that does not admit the App).</summary>
    public Dictionary<string, string> BranchRefusals { get; } = new(StringComparer.Ordinal);

    public Task<ForgeWriteOutcome> CreateBranchAsync(Repository repository, string branch, string sha, CancellationToken cancellationToken = default)
    {
        if (BranchRefusals.TryGetValue(branch, out var refusal)) throw new ForgeRequestException(refusal, 422);
        if (Branches.Any(b => b.Branch == branch)) return Task.FromResult(ForgeWriteOutcome.Exists);
        Branches.Add((branch, sha));
        return Task.FromResult(ForgeWriteOutcome.Created);
    }

    /// <summary>The heads of the pull requests opened as drafts.</summary>
    public List<string> DraftHeads { get; } = [];

    public Task<int> OpenPullRequestAsync(Repository repository, string head, string @base, string title, string body, bool draft = false,
        CancellationToken cancellationToken = default)
    {
        if (draft) DraftHeads.Add(head);
        var index = OpenedPullRequests.FindIndex(p => p.Head == head && p.Base == @base);
        if (index < 0)
        {
            OpenedPullRequests.Add((head, @base, title, body));
            index = OpenedPullRequests.Count - 1;
        }
        return Task.FromResult(1000 + index);
    }

    public Task<IReadOnlyList<int>> MergedPullRequestsAsync(Repository repository, string sha, string baseBranch, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<int>>(MergedPullRequests.TryGetValue(sha, out var numbers) ? numbers : []);

    public Task CommentAsync(Repository repository, int number, string body, CancellationToken cancellationToken = default)
    {
        Comments.Add((number, body));
        return Task.CompletedTask;
    }

    public List<(string Sha, string Body)> CommitComments { get; } = [];

    public Task CommentOnCommitAsync(Repository repository, string sha, string body, CancellationToken cancellationToken = default)
    {
        CommitComments.Add((sha, body));
        return Task.CompletedTask;
    }

    public Task DispatchWorkflowAsync(Repository repository, string workflowFile, string branch, CancellationToken cancellationToken = default)
    {
        Dispatches.Add((workflowFile, branch));
        return Task.CompletedTask;
    }
}
