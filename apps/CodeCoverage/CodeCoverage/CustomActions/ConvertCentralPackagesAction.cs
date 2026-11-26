using System.Net;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Onboarding;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// multi-target-dependency-split-PRD P7 (M7; owner, 2026-11-15: "a new CustomAction that lets the user switch between non-CPVM
/// and CPVM on a (one or more selected) repository - it also creates the pull-request"): a query action on the repository
/// lists (<c>selectionRule ">0"</c>) that asks one direction for the run, then opens one pull request per selected repository
/// with <see cref="CentralPackageConversion"/>'s edits (<see cref="IOnboardingService.ConvertCentralPackagesAsync"/>). It is
/// separate from the dependency updater, which never converts on its own.
/// </summary>
/// <remarks>
/// Like <see cref="SetUpRulesetsAction"/>: only repositories the caller manages, and partial results are reported, never
/// hidden (one notification per repository, and the same in the result for API callers).
/// </remarks>
public partial class ConvertCentralPackagesAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IOnboardingService onboarding;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.SelectedItems.Select(i => i.Id).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            return;

        var to = manager.GetTranslatedMessage("centralPackages.to");
        var from = manager.GetTranslatedMessage("centralPackages.from");
        manager.Retry.Action(manager.GetTranslatedMessage("centralPackages.chooseTitle"), [to, from], to,
            message: WebUtility.HtmlEncode(manager.GetTranslatedMessage("centralPackages.chooseMessage")), cancellable: true);
        if (manager.Retry.Result!.Option is not { } option || option == RetryResult.CancelOption)
            return;
        var direction = option == from ? ECentralPackageDirection.FromCentral : ECentralPackageDirection.ToCentral;

        var results = new List<CentralPackageRepositoryResult>();
        var loaded = await session.LoadAsync<Repository>(ids, cancellationToken);
        results.AddRange(ids.Where(id => loaded.GetValueOrDefault(id) is null)
            .Select(id => new CentralPackageRepositoryResult(id, id, false, manager.GetTranslatedMessage("centralPackages.reason.notFound"), null)));
        var repositories = ids.Select(id => loaded.GetValueOrDefault(id)).OfType<Repository>().ToList();
        var managed = await permissions.CheckAsync(repositories, cancellationToken);
        foreach (var repository in repositories)
        {
            if (managed.GetValueOrDefault(repository.Id.Value) is null)
            {
                results.Add(new(repository.Id.Value, repository.FullName, false, manager.GetTranslatedMessage("centralPackages.reason.notManaged"), null));
                continue;
            }
            var outcome = await onboarding.ConvertCentralPackagesAsync(repository, direction, cancellationToken);
            results.Add(new(repository.Id.Value, repository.FullName, outcome.Opened, outcome.Note, outcome.PullRequestUrl));
        }

        foreach (var result in results)
            manager.Client.Notify(result.Opened
                    ? manager.GetTranslatedMessage("centralPackages.opened", result.Name, result.PullRequestUrl ?? result.Note)
                    : manager.GetTranslatedMessage("centralPackages.notOpened", result.Name, result.Note),
                result.Opened ? NotificationKind.Success : NotificationKind.Warning);
        args.SetResult(new ConvertCentralPackagesResult(direction, results));
    }
}

/// <summary>What <see cref="ConvertCentralPackagesAction"/> returns (the response envelope's <c>result</c>).</summary>
public sealed record ConvertCentralPackagesResult(ECentralPackageDirection Direction, IReadOnlyList<CentralPackageRepositoryResult> Repositories);

/// <param name="Opened">A pull request is open with the conversion.</param>
/// <param name="Note">The pull request, or why there is none.</param>
public sealed record CentralPackageRepositoryResult(string Id, string Name, bool Opened, string Note, string? PullRequestUrl);
