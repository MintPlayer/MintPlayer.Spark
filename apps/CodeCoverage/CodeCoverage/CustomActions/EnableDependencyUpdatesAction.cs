using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// "Enable dependency updates" (dependency-updates PRD R22, R23, R25, R26): a query action on the onboarding page's query
/// (<c>Repository_DependencyOnboarding</c>, <c>selectionRule ">0"</c>). For every selected repository it re-checks that the
/// caller may (R21: a manager of its account, or an administrator of it on the forge, asked now), then sets the
/// repository's <c>DependencyUpdates</c> override to Enabled and its onboarding merge-queue setting, and saves through
/// Spark. The save is the one trigger path: the after-commit hook onboards it (R23).
/// </summary>
/// <remarks>
/// <para>
/// <b>The merge-queue choice is the action's argument</b> (R25), asked as a retry prompt: "with the merge queue" or "without",
/// defaulting to the selected accounts' setting. The page can answer it directly. Everything before the prompt only reads,
/// so the pass that carries the answer repeats it harmlessly.
/// </para>
/// <para>
/// <b>Partial results are reported, never hidden</b> (R22): the result lists what was enabled and, per refused repository,
/// why ("you don't manage it", "the save was refused: …").
/// </para>
/// </remarks>
public partial class EnableDependencyUpdatesAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService onboarding;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;
    [Inject] private readonly ILogger<EnableDependencyUpdatesAction> logger;

    /// <summary>The <c>Repository</c> persistent object's type id (App_Data/Model/Repository.json).</summary>
    internal static readonly Guid RepositoryType = Guid.Parse("22880468-80f5-4fd2-9472-4f87842ce4ff");

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.SelectedItems.Select(i => i.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return;

        var loaded = await session.LoadAsync<Repository>(ids, cancellationToken);
        var repositories = ids.Select(id => loaded.GetValueOrDefault(id)).OfType<Repository>().ToList();
        var reasons = await onboarding.CheckAsync(repositories, cancellationToken);
        var allowed = repositories.Where(r => reasons.GetValueOrDefault(r.Id!) is not null).ToList();
        var refused = repositories.Where(r => reasons.GetValueOrDefault(r.Id!) is null)
            .Select(r => new DependencyOnboardingRefusal(r.Id!, r.FullName, "you don't manage this repository")).ToList();
        refused.AddRange(ids.Where(id => !loaded.ContainsKey(id) || loaded[id] is null).Select(id => new DependencyOnboardingRefusal(id, id, "no such repository")));

        bool withMergeQueue = true;
        if (allowed.Count > 0)
        {
            var accounts = await session.LoadAsync<Account>(allowed.Select(r => r.Account).OfType<string>().Distinct(), cancellationToken);
            var defaultYes = allowed.Any(r => Repository.ResolveDependencyOnboardingMergeQueue(r, r.Account is { } a ? accounts.GetValueOrDefault(a) : null));
            var with = manager.GetTranslatedMessage("dependencyOnboarding.withMergeQueue");
            var without = manager.GetTranslatedMessage("dependencyOnboarding.withoutMergeQueue");
            manager.Retry.Action(
                title: manager.GetTranslatedMessage("dependencyOnboarding.mergeQueueQuestion"),
                options: [with, without],
                defaultOption: defaultYes ? with : without,
                cancellable: true);
            var answer = manager.Retry.Result!;
            if (answer.Option == RetryResult.CancelOption)
                return;
            withMergeQueue = answer.Option != without;
        }

        var enabled = new List<string>();
        var objects = allowed.Count == 0 ? [] : await databaseAccess.GetPersistentObjectsByIdAsync(RepositoryType, [.. allowed.Select(r => r.Id!)]);
        foreach (var repository in allowed)
        {
            if (objects.FirstOrDefault(o => string.Equals(o.Id, repository.Id, StringComparison.Ordinal)) is not { } po)
            {
                refused.Add(new DependencyOnboardingRefusal(repository.Id!, repository.FullName, "you don't manage this repository"));
                continue;
            }
            po[nameof(Repository.DependencyUpdates)].Value = nameof(EDependencyUpdatesPolicy.Enabled);
            po[nameof(Repository.DependencyOnboardingMergeQueue)].Value = withMergeQueue
                ? nameof(EDependencyOnboardingMergeQueuePolicy.Yes)
                : nameof(EDependencyOnboardingMergeQueuePolicy.No);
            try
            {
                await databaseAccess.SavePersistentObjectAsync(po);
                enabled.Add(repository.FullName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dependency onboarding: could not enable {Repository}", repository.FullName);
                refused.Add(new DependencyOnboardingRefusal(repository.Id!, repository.FullName, $"the save was refused: {ex.Message}"));
            }
        }

        args.SetResult(new DependencyOnboardingResult(enabled, refused, withMergeQueue));
        if (enabled.Count > 0)
            manager.Client.Notify(manager.GetTranslatedMessage("dependencyOnboarding.enabled", enabled.Count), NotificationKind.Success);
        if (refused.Count > 0)
            manager.Client.Notify(manager.GetTranslatedMessage("dependencyOnboarding.notEnabled",
                string.Join("; ", refused.Select(r => $"{r.FullName}: {r.Reason}"))), NotificationKind.Warning);
    }
}

/// <summary>What <see cref="EnableDependencyUpdatesAction"/> returns (the response envelope's <c>result</c>).</summary>
/// <param name="Enabled">The repositories switched on, by full name; each is onboarded by its after-commit hook.</param>
/// <param name="NotEnabled">Every selected repository that was not, with why.</param>
public sealed record DependencyOnboardingResult(IReadOnlyList<string> Enabled, IReadOnlyList<DependencyOnboardingRefusal> NotEnabled, bool WithMergeQueue);

public sealed record DependencyOnboardingRefusal(string RepositoryId, string FullName, string Reason);
