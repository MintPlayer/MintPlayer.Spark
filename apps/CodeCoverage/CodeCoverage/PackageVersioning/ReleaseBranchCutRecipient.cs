using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>Cut the release branches one server-host versioning run asks for (docs/coverage_package_versioning_PRD.md D12).</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record CutReleaseBranchesMessage
{
    /// <summary>The <see cref="VersioningRun"/> document id.</summary>
    public required string RunId { get; init; }
}

/// <summary>
/// Creates <c>release/&lt;old major&gt;.x</c> at the release commit's first parent when a platform major moved (D12), and tells
/// the merged pull request so. A branch that exists already is left as it is: the first package to leave a major cut it
/// for the whole line. Each outcome is saved as it lands, so a message parked by a rate limit resumes after the last one.
/// </summary>
/// <remarks>The auto-cut policy is read here, not when the run was recorded, so switching it off stops a parked run.</remarks>
public partial class ReleaseBranchCutRecipient : IRecipient<CutReleaseBranchesMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly ILogger<ReleaseBranchCutRecipient> logger;

    public async Task HandleAsync(CutReleaseBranchesMessage message, CancellationToken cancellationToken = default)
    {
        var run = await session.LoadAsync<VersioningRun>(message.RunId, cancellationToken);
        if (run is null) return;
        var repository = await session.LoadAsync<Repository>(run.Repository, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        var enabled = Repository.ResolveAutoCutReleaseBranches(repository, account).Enabled;

        foreach (var cut in run.BranchCuts.Where(c => c.Outcome is null))
        {
            // The writer named the branch from the repository's own patterns; a name that is not a plain ref is never sent on.
            if (!enabled || !IsBranchName(cut.Branch) || !GitCli.IsObjectId(cut.Commit))
            {
                cut.Outcome = enabled ? "invalid" : "disabled";
                await session.SaveChangesAsync(cancellationToken);
                continue;
            }

            var outcome = await forge.CreateBranchAsync(repository, cut.Branch, cut.Commit, cancellationToken);
            cut.Outcome = outcome == ForgeWriteOutcome.Created ? "created" : "exists";
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Versioning: release branch {Branch} of {Repository}: {Outcome}", cut.Branch, repository.FullName, cut.Outcome);

            if (outcome == ForgeWriteOutcome.Created && cut.PullRequest is { } number)
                await forge.CommentAsync(repository, number, Comment(cut), cancellationToken);
        }
    }

    internal static bool IsBranchName(string branch) => VersioningConfig.IsBranchPattern(branch) && !branch.Contains('*');

    internal static string Comment(VersioningRunBranchCut cut)
    {
        var major = cut.Major.ToString(CultureInfo.InvariantCulture);
        // "\n" spelled out: a raw literal would carry the checkout's line endings. The action's cutComment is the same text.
        return $"{string.Join(", ", cut.Packages.Select(p => $"`{p}`"))} left major {major}, so `{cut.Branch}` was cut at {cut.Commit[..12]}: "
            + $"patches for the {major}.x line merge there and are versioned as {major}.x.\n\n"
            + $"To backport a merged pull request, label it `backport: {major}.x` or comment `/backport {major}.x`. "
            + "The branch is protected only when a ruleset matches it.";
    }
}
