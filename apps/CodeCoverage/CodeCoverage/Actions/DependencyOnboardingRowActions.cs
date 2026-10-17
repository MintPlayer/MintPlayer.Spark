using CodeCoverage.Dependencies.Onboarding;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Queries;

namespace CodeCoverage.Actions;

/// <summary>
/// One row of the onboarding page's grid (dependency-updates PRD R22, D-O14): a repository the viewer manages, with its
/// dependency-update state. A virtual type (App_Data/Model/DependencyOnboardingRow.json, no clrType): the rows are computed
/// per request and per viewer, never stored.
/// </summary>
/// <param name="Id">The <c>Repository</c> document id, the id the query action <c>EnableDependencyUpdates</c> takes.</param>
/// <param name="ManagedBy">Why the viewer may enable it, translated (R39: account owner, or repository admin). Re-checked when
/// the action executes, never taken from here.</param>
/// <param name="State">Off, syncing, or the onboarding's state, translated.</param>
/// <param name="OnboardingPullRequest">The onboarding pull request's URL, or <c>#n</c> when the forge gives none; null without one.</param>
/// <param name="MergeQueueInCoverageYml">R26: <c>coverage.yml</c>'s merge queue as last read, translated.</param>
/// <param name="OnboardingMergeQueue">R26: whether the onboarding pull request would add the merge queue, translated.</param>
public sealed record DependencyOnboardingRow(
    string Id, string FullName, bool IsPrivate, string ManagedBy, string State, string? OnboardingPullRequest, string? Note,
    string MergeQueueInCoverageYml, string OnboardingMergeQueue);

/// <summary>
/// Backs <c>Custom.DependencyOnboardingRows</c>, the onboarding page's grid (D-O14). The framework searches, sorts, pages and
/// keeps the selection; the query action <c>EnableDependencyUpdates</c> runs on it.
/// </summary>
public partial class DependencyOnboardingRowActions : ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Rows are GENERATED from the caller's own management rights rather than filtered afterwards: " +
        "IDependencyOnboardingService.ListAsync (Dependencies/Onboarding/DependencyOnboardingService.cs) lists the " +
        "repositories of the forge installations the caller's own credential reaches, and keeps only those whose account the " +
        "caller manages (ISparkVisibility.CanManageOwnerAsync, R39) or that the forge says the caller administers (R33). The " +
        "parent DependencyOnboardingScope only narrows that list to the installation its setup-redirect query names; an " +
        "installation of someone else, or a malformed one, narrows it to nothing (D-O13). An anonymous caller is refused " +
        "before this runs (Query/DependencyOnboardingRow is granted to the authenticated role only), and the query action " +
        "re-checks every selected row when it executes.";

    [Inject] private readonly IDependencyOnboardingService onboarding;
    [Inject] private readonly IManager manager;

    /// <summary>The viewer's managed repositories that are in this app (a Repository document), narrowed by the scope.</summary>
    /// <remarks>
    /// The repositories still syncing have no document, so no id the action could take: the page counts them in a banner
    /// from the header endpoint instead. An in-memory queryable, so every column sorts and searches.
    /// </remarks>
    public async Task<IQueryable<DependencyOnboardingRow>> DependencyOnboardingRows(CustomQueryArgs args)
    {
        // Only the scope type narrows; any other parent is ignored, which lists the viewer's own repositories unnarrowed.
        var scope = string.Equals(args.Parent?.Name, DependencyOnboardingScope.TypeName, StringComparison.Ordinal) ? args.Parent!.Id : null;
        var listing = await onboarding.ListAsync(DependencyOnboardingScope.Parse(scope));
        return listing.Repositories
            .Where(r => r.RepositoryId is not null)
            .DistinctBy(r => r.RepositoryId, StringComparer.Ordinal)
            .Select(ToRow)
            .ToList()
            .AsQueryable();
    }

    private DependencyOnboardingRow ToRow(DependencyOnboardingEntry entry) => new(
        entry.RepositoryId!,
        entry.FullName,
        entry.IsPrivate,
        T($"dependencyOnboarding.managedBy.{entry.ManagedBy}", entry.ManagedBy),
        T($"dependencyOnboarding.state.{StateKey(entry)}", entry.Onboarding),
        entry.OnboardingPullRequest is { } pr ? pr.Url ?? $"#{pr.Number}" : null,
        entry.OnboardingNote ?? LastRun(entry),
        T($"dependencyOnboardingUi.mergeQueueInFile.{entry.MergeQueueInFile ?? "unknown"}", entry.MergeQueueInFile ?? "unknown"),
        T(entry.OnboardingMergeQueue ? "dependencyOnboarding.queueWillAdd" : "dependencyOnboarding.queueWontAdd",
            entry.OnboardingMergeQueue ? "yes" : "no"));

    /// <summary>
    /// Syncing (no default branch yet), off (the setting resolves off), or the onboarding's own state. A repository onboarded
    /// once and switched off since keeps <c>Done</c> on its run document; the row follows the setting, not that history (P1,
    /// production re-test 2026-10-17).
    /// </summary>
    internal static string StateKey(DependencyOnboardingEntry entry)
        => entry.Syncing ? "syncing"
            : !entry.Enabled ? "off"
            : entry.Onboarding;

    /// <summary>
    /// D-O20 (live test 2026-10-17): once onboarding has nothing to say, the Note says how the last run ended, so a run
    /// that opened no pull request ("nothing within your thresholds; 12 groups awaiting approval", "the git writer is not
    /// configured") is not silence on the page that started it.
    /// </summary>
    private string? LastRun(DependencyOnboardingEntry entry)
    {
        if (entry.LastRunOutcome is not { } outcome)
            return null;
        var message = entry.LastRunMessage ?? string.Empty;
        var text = manager.GetTranslatedMessage("dependencyOnboarding.lastRun", outcome, message);
        return string.IsNullOrEmpty(text) || text == "dependencyOnboarding.lastRun" ? $"Last run: {outcome}: {message}" : text;
    }

    private string T(string key, string fallback)
    {
        var text = manager.GetTranslatedMessage(key);
        return string.IsNullOrEmpty(text) || text == key ? fallback : text;
    }
}

/// <summary>
/// The onboarding grid's parent (D-O14): a virtual page whose id is the setup redirect's query string. Scaffolded for any id,
/// so an installation of someone else, a malformed one and one that does not exist load alike and the rows tell them apart
/// by nothing (D-O13). It carries no values: <see cref="DependencyOnboardingRowActions"/> reads only its id.
/// </summary>
public partial class DependencyOnboardingScopeActions
{
    [Inject] private readonly IManager manager;

    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = await manager.GetPersistentObjectAsync(DependencyOnboardingScope.TypeName);
        obj.Id = id;
        return obj;
    }
}
