using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
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
            var outcome = await forge.CreateReleaseAsync(repository, new ForgeRelease(release.Tag, notes.Name, notes.Body, notes.Prerelease), cancellationToken);
            release.ReleaseOutcome = outcome == ForgeWriteOutcome.Created ? "created" : "exists";
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Versioning: Release {Tag} of {Repository}: {Outcome}", release.Tag, repository.FullName, release.ReleaseOutcome);
        }
    }
}
