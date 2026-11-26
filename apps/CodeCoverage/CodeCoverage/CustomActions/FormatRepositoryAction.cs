using System.Net;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Formatting;
using CodeCoverage.Onboarding;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>#657 D3: formats the selected repositories with their own formatters, one pull request each.</summary>
public partial class FormatRepositoryAction : FormattingActionBase
{
    protected override string Mode => FormattingWorkflow.FormatMode;
}

/// <summary>#657 D3: fixes what the selected repositories' linters can fix, one pull request each.</summary>
public partial class LintRepositoryAction : FormattingActionBase
{
    protected override string Mode => FormattingWorkflow.LintMode;
}

/// <summary>
/// The two formatting actions (#657 D3, D5, D14): query actions on the repository lists, like
/// <see cref="ConvertCentralPackagesAction"/>. For each selected repository the caller manages, and whose default branch
/// has <c>formatting.yml</c> (D1), the run is dispatched (<see cref="IFormatRunEngine"/>); its pull request and the outcome
/// arrive later, as a notification. When one of them has the merge queue, the caller first chooses whether the pull
/// requests go to the queue once they open (D5).
/// </summary>
public abstract partial class FormattingActionBase : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IOnboardingService onboarding;
    [Inject] private readonly IFormatRunEngine engine;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkCurrentPrincipal currentUser;
    [Inject] private readonly IEnumerable<IForgeRepositoryAdministration> administrations;

    protected abstract string Mode { get; }

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.SelectedItems.Select(i => i.Id).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            return;

        var results = new List<FormattingRepositoryResult>();
        var loaded = await session.LoadAsync<Repository>(ids, cancellationToken);
        results.AddRange(ids.Where(id => loaded.GetValueOrDefault(id) is null)
            .Select(id => new FormattingRepositoryResult(id, id, false, manager.GetTranslatedMessage("formatting.reason.notFound"))));
        var repositories = ids.Select(id => loaded.GetValueOrDefault(id)).OfType<Repository>().ToList();
        var managed = await permissions.CheckAsync(repositories, cancellationToken);

        var ready = new List<(Repository Repository, FormattingTarget Target)>();
        foreach (var repository in repositories)
        {
            if (managed.GetValueOrDefault(repository.Id.Value) is null)
            {
                results.Add(new(repository.Id.Value, repository.FullName, false, manager.GetTranslatedMessage("formatting.reason.notManaged")));
                continue;
            }
            var (target, refusal) = await onboarding.FormattingTargetAsync(repository, cancellationToken);
            if (target is null)
                results.Add(new(repository.Id.Value, repository.FullName, false, refusal ?? manager.GetTranslatedMessage("formatting.reason.unreadable")));
            else if (target.Workflow is null)
                results.Add(new(repository.Id.Value, repository.FullName, false, manager.GetTranslatedMessage("formatting.reason.notSetUp")));
            else
                ready.Add((repository, target));
        }

        // D5: asked once, and only when a repository has the merge queue.
        var enqueue = false;
        if (ready.Any(r => r.Target.MergeQueue))
        {
            var open = manager.GetTranslatedMessage("formatting.open");
            var queue = manager.GetTranslatedMessage("formatting.openAndQueue");
            manager.Retry.Action(manager.GetTranslatedMessage("formatting.chooseTitle"), [open, queue], open,
                message: WebUtility.HtmlEncode(manager.GetTranslatedMessage("formatting.chooseMessage")), cancellable: true);
            if (manager.Retry.Result!.Option is not { } option || option == RetryResult.CancelOption)
                return;
            enqueue = option == queue;
        }

        var logins = new Dictionary<EForgeProvider, string?>();
        foreach (var (repository, target) in ready)
        {
            string? login = null;
            if (enqueue && target.MergeQueue)
            {
                if (!logins.TryGetValue(repository.Provider, out login))
                {
                    login = administrations.FirstOrDefault(a => a.Provider == repository.Provider) is { } administration
                        ? (await administration.GetViewerLoginAsync(cancellationToken)).Login : null;
                    logins[repository.Provider] = login;
                }
            }
            var started = await engine.StartAsync(repository, new FormatRun
            {
                Mode = Mode, RequestedBy = currentUser.Id, RequestedByLogin = login, Enqueue = login is not null,
                Branch = target.Branch, Sha = target.Sha, Exclude = [.. FormattingWorkflow.Exclude(target.Workflow)],
            }, cancellationToken);
            results.Add(started
                ? new(repository.Id.Value, repository.FullName, true, manager.GetTranslatedMessage("formatting.started"))
                : new(repository.Id.Value, repository.FullName, false, manager.GetTranslatedMessage("formatting.reason.running")));
        }

        foreach (var result in results)
            manager.Client.Notify(result.Started
                    ? manager.GetTranslatedMessage("formatting.startedOn", result.Name)
                    : manager.GetTranslatedMessage("formatting.notStarted", result.Name, result.Note),
                result.Started ? NotificationKind.Info : NotificationKind.Warning);
        args.SetResult(new FormattingResult(Mode, results));
    }
}

/// <summary>What a formatting action returns (the response envelope's <c>result</c>).</summary>
public sealed record FormattingResult(string Mode, IReadOnlyList<FormattingRepositoryResult> Repositories);

/// <param name="Started">The run is dispatched; its pull request or outcome arrives as a notification.</param>
public sealed record FormattingRepositoryResult(string Id, string Name, bool Started, string Note);
