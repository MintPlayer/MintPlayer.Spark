using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>Create the GitHub Releases of one server-host versioning run (docs/coverage_package_versioning_PRD.md D15).</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record CreateVersionReleasesMessage
{
    /// <summary>The <see cref="VersioningRun"/> document id.</summary>
    public required string RunId { get; init; }
}

/// <summary>
/// One Release per tag the run put in place, created one at a time (D15): GitHub's secondary rate limits punish bursts of
/// content-creating requests, so the recipient waits <see cref="Spacing"/> between two. A rate limit it meets anyway
/// throws <see cref="ForgeRateLimitedException"/>, which parks the message until the limit lifts. Each outcome is saved as
/// it lands, so the retry resumes after the last Release made, and a tag that already has a Release counts as done.
/// </summary>
/// <remarks>The release-on-tag policy is read here, not when the run was recorded, so switching it off stops a parked run.</remarks>
public partial class VersionReleaseRecipient : IRecipient<CreateVersionReleasesMessage>
{
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<VersionReleaseRecipient> logger;

    public async Task HandleAsync(CreateVersionReleasesMessage message, CancellationToken cancellationToken = default)
    {
        var run = await session.LoadAsync<VersioningRun>(message.RunId, cancellationToken);
        if (run is null) return;
        var repository = await session.LoadAsync<Repository>(run.Repository, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolveReleaseOnTag(repository, account).Enabled) return;

        var first = true;
        foreach (var release in run.Releases.Where(r => r.Release is not null && r.ReleaseOutcome is null))
        {
            if (!first) await Task.Delay(Spacing, clock, cancellationToken);
            first = false;
            var notes = release.Release!;
            // #693 D7: a package that moved only with a dependency gets no Release of its own; the run's Release lists it.
            if (notes.DependencyOnly)
            {
                release.ReleaseOutcome = "listed";
                await session.SaveChangesAsync(cancellationToken);
                continue;
            }
            var outcome = await forge.CreateReleaseAsync(repository, new ForgeRelease(release.Tag, notes.Name, notes.Body, notes.Prerelease), cancellationToken);
            release.ReleaseOutcome = outcome == ForgeWriteOutcome.Created ? "created" : "exists";
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Versioning: Release {Tag} of {Repository}: {Outcome}", release.Tag, repository.FullName, release.ReleaseOutcome);
        }

        await CreateRunReleaseAsync(run, repository, forge, first, cancellationToken);
    }

    /// <summary>
    /// #693 D7: the run's own Release, last and marked latest. Its date is the run's start, so a retry names the same tag;
    /// a candidate whose tag already has a Release is another run's, and the next <c>.n</c> is tried.
    /// </summary>
    private async Task CreateRunReleaseAsync(VersioningRun run, Repository repository, IForgeVersioning forge, bool first, CancellationToken cancellationToken)
    {
        if (run.RunReleaseTag is not null) return;
        var packages = run.Releases.Where(r => r.Release is not null)
            .Select(r => new RunReleasePackage(r.Id, r.Version, r.Tag, r.Release!.DependencyOnly)).ToList();
        if (packages.Count == 0) return;

        var date = DateOnly.FromDateTime(run.StartedAtUtc);
        var tagBase = RunRelease.TagBase(run.Branch, repository.DefaultBranch, date);
        var url = repository.Provider == EForgeProvider.GitHub ? $"https://github.com/{repository.FullName}" : "";
        var release = new ForgeRelease(tagBase, RunRelease.Name(run.Branch, repository.DefaultBranch, date), RunRelease.Body(packages, url), false, run.Commit, Latest: true);
        for (var n = 1; n <= RunRelease.MaxPerDay; n++)
        {
            if (!first) await Task.Delay(Spacing, clock, cancellationToken);
            first = false;
            var tag = RunRelease.Tag(tagBase, n);
            if (await forge.CreateReleaseAsync(repository, release with { TagName = tag }, cancellationToken) != ForgeWriteOutcome.Created) continue;
            run.RunReleaseTag = tag;
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Versioning: run Release {Tag} of {Repository}", tag, repository.FullName);
            return;
        }
        logger.LogWarning("Versioning: every run Release tag from {Tag} on is taken in {Repository}", tagBase, repository.FullName);
    }
}
