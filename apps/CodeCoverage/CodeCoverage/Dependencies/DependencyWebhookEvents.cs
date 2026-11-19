using System.ComponentModel;
using CodeCoverage.Entities;
using MintPlayer.Spark.Apps;

namespace CodeCoverage.Dependencies;

/// <summary>#691 D41: the dependency updater's run an event is about.</summary>
public sealed record DependencyRun(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    [property: Description("The release line the run updates; null for the default branch.")] string? Line,
    [property: Description("The branch the update pull request merges into.")] string? BaseBranch,
    int Run,
    [property: Description("What started the run, e.g. Schedule or Refresh.")] string Trigger)
{
    internal static DependencyRun Of(DependencyUpdateRun run, Repository repository) => new(
        repository.FullName, repository.Provider.ToString().ToLowerInvariant(), run.Line, run.DefaultBranch, run.Run, run.Trigger.ToString());
}

/// <summary>#691 D41: the dependency updater opened its rolling update pull request.</summary>
[WebhookEvent("dependencies.pull_request.opened", Permission = "dependencies:read", Description = "The dependency updater opened its update pull request.")]
public sealed record DependencyPullRequestOpened(
    DependencyRun Run,
    int Number,
    string Title,
    [property: Description("The update branch.")] string Branch,
    [property: Description("The first pull request after onboarding is a draft and never merges on its own.")] bool Draft,
    DateTimeOffset OpenedAt);

/// <summary>#691 D41: a dependency-update run ended.</summary>
[WebhookEvent("dependencies.run.completed", Permission = "dependencies:read", Description = "A dependency-update run ended, with its outcome.")]
public sealed record DependencyRunCompleted(
    DependencyRun Run,
    [property: Description("e.g. green (handed to the merge queue), published, nothing-planned, red, bisected, checks-timeout, error.")] string Outcome,
    string Message,
    [property: Description("The update pull request, when the run had one.")] int? PullRequest,
    DateTimeOffset CompletedAt);
