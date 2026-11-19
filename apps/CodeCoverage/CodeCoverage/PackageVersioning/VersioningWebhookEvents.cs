using System.ComponentModel;
using CodeCoverage.Entities;
using MintPlayer.Spark.Apps;

namespace CodeCoverage.PackageVersioning;

/// <summary>One package version a versioning run computed.</summary>
public sealed record VersioningPackage(
    [property: Description("The package id, e.g. MintPlayer.Spark or @mintplayer/ng-spark.")] string Id,
    [property: Description("nuget, npm, maven, …")] string Kind,
    string Version,
    [property: Description("The tag the run placed, <id>/v<version>.")] string Tag);

/// <summary>#691 D41: the versioning action's run reported its result (versions computed, tags placed).</summary>
[WebhookEvent("versioning.run.completed", Permission = "versioning:read", Description = "A versioning run computed the packages' next versions and placed their tags.")]
public sealed record VersioningRunResult(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    string Branch,
    string Commit,
    [property: Description("The CI run, as runId.attempt.")] string Run,
    [property: Description("The run's outcome as the action reported it.")] string? Outcome,
    string? Reason,
    IReadOnlyList<VersioningPackage> Packages,
    [property: Description("The packages the run refused to version.")] IReadOnlyList<string> Refused,
    DateTimeOffset CompletedAt)
{
    internal static VersioningRunResult Of(VersioningRun run, Repository repository, DateTime nowUtc) => new(
        repository.FullName, repository.Provider.ToString().ToLowerInvariant(), run.Branch, run.Commit, $"{run.RunId}.{run.RunAttempt}",
        run.Outcome, run.Reason, [.. run.Releases.Select(r => new VersioningPackage(r.Id, r.Kind, r.Version, r.Tag))], [.. run.Refused.Select(r => r.Id)],
        new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)));
}

/// <summary>#691 D41: an attempt to backport a merged pull request onto a release line, with its outcome.</summary>
[WebhookEvent("backport.attempted", Permission = "versioning:read", Description = "A backport of a merged pull request onto a release line was attempted: opened, drafted, conflicted, failed, held, …")]
public sealed record BackportAttempted(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    [property: Description("The backported pull request.")] int Source,
    string? SourceSha,
    [property: Description("The release line.")] string Line,
    [property: Description("One of: 'opened'; 'opened, not regenerated'; 'draft, conflicts'; 'regenerating'; 'already on the line'; 'branch exists'; 'conflicts' (a person backports it by hand); 'failed'; 'held'.")] string Outcome,
    [property: Description("The backport pull request, when one was opened.")] int? PullRequest,
    string? Detail,
    DateTimeOffset AttemptedAt);

/// <summary>#691 D41: a backport pull request merged into its release line.</summary>
[WebhookEvent("backport.merged", Permission = "versioning:read", Description = "A backport pull request merged into its release line.")]
public sealed record BackportMerged(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    [property: Description("The backport pull request.")] int Number,
    [property: Description("The pull request it backports.")] int Source,
    [property: Description("The release line.")] string Line,
    DateTimeOffset MergedAt);
