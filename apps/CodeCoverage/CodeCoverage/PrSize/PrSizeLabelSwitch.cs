using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PrSize;

/// <summary>
/// What an Account or Repository save means for the labels already on the forge (docs/coverage_pr_size_labels_PRD.md D4.2,
/// D4.3): a changed scale reconciles them, and switching the labeller off asks whether to remove them.
/// </summary>
/// <remarks>
/// The answer travels from <c>OnBeforeSaveAsync</c> to <c>OnAfterSaveCommittedAsync</c> as a save fact, and every forge
/// write happens after the commit, on the labeller's lane. A Cancel throws <see cref="SparkCancelException"/> and stores
/// nothing.
/// </remarks>
public static class PrSizeLabelSwitch
{
    /// <summary>The fact: present when the save asks the forge to change, its value one of the two below.</summary>
    public const string Fact = "PrSizeLabels";

    /// <summary>The scale changed: reconcile every repository that has labels.</summary>
    public const string Reconcile = "reconcile";

    /// <summary>Switched off, and the user chose to remove the labels.</summary>
    public const string Remove = "remove";

    public const string RemoveOption = "Remove labels";
    public const string KeepOption = "Keep labels";

    private const string Consequence = "Pull requests that carry them, including closed ones, lose them.";

    /// <summary>
    /// An account's save: asks on switching off when inheriting repositories have labels, and otherwise notes a changed
    /// scale. Null when the forge needs nothing.
    /// </summary>
    public static async Task<string?> DecideAsync(IManager manager, IAsyncDocumentSession session, Account? before, Account after)
    {
        if (before is { PrSizeLabels: true } && !after.PrSizeLabels && after.Id is { } accountId)
        {
            var labelled = (await RepositoriesWithLabelsAsync(session, accountId, default))
                .Count(r => r.PrSizeLabels == EPrSizeLabelPolicy.Inherit);
            if (labelled > 0 && Ask(manager,
                    $"Also remove the size labels from the {labelled} {(labelled == 1 ? "repository" : "repositories")} that inherit this setting? {Consequence}"))
                return Remove;
        }
        return before is not null && ScaleChanged(before.PrSizeLabelScale, after.PrSizeLabelScale) ? Reconcile : null;
    }

    /// <summary>A repository's save: asks when its effective setting goes off while it has labels.</summary>
    public static string? Decide(IManager manager, Repository? before, Repository after, Account? account)
    {
        var was = before is not null && Repository.ResolvePrSizeLabels(before, account).Enabled;
        var now = Repository.ResolvePrSizeLabels(after, account).Enabled;
        if (!was || now || after.PrSizeLabelState.Count == 0) return null;
        return Ask(manager, $"Also remove the size labels from {after.FullName}? {Consequence}") ? Remove : null;
    }

    /// <summary>After an account's commit: one message per repository with labels, paced by the lane (D4.2).</summary>
    public static async Task<int> FanOutAsync(IAsyncDocumentSession session, IMessageBus bus, string accountId, string decision, CancellationToken ct)
    {
        var repositories = await RepositoriesWithLabelsAsync(session, accountId, ct);
        var count = 0;
        foreach (var repository in repositories)
        {
            if (decision == Remove && repository.PrSizeLabels == EPrSizeLabelPolicy.Inherit)
                await bus.BroadcastAsync(new RemovePrSizeLabelsMessage { RepositoryId = repository.Id! }, ct);
            else if (decision == Reconcile)
                await bus.BroadcastAsync(new ReconcilePrSizeLabelsMessage { RepositoryId = repository.Id! }, ct);
            else
                continue;
            count++;
        }
        return count;
    }

    /// <summary>Whether the labels as the forge should carry them differ: names, colours, descriptions or thresholds.</summary>
    public static bool ScaleChanged(IReadOnlyList<PrSizeLabel> before, IReadOnlyList<PrSizeLabel> after)
    {
        var was = new PrSizeScale(before);
        var now = new PrSizeScale(after);
        return !was.Rows.Select(was.Desired).Select(Key).SequenceEqual(now.Rows.Select(now.Desired).Select(Key));

        static (string, string, string, string) Key(ManagedLabel l) => (l.RowId, l.Name, l.Color, l.Description);
    }

    private static bool Ask(IManager manager, string message)
    {
        manager.Retry.Action(title: "Remove the size labels?", options: [RemoveOption, KeepOption], message: message);
        return manager.Retry.Result!.Option switch
        {
            RemoveOption => true,
            KeepOption => false,
            _ => throw new SparkCancelException(),
        };
    }

    private static async Task<List<Repository>> RepositoriesWithLabelsAsync(IAsyncDocumentSession session, string accountId, CancellationToken ct)
    {
        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == accountId)
            .Take(1024)
            .ToListAsync(ct);
        return repositories.Where(r => r.Id is not null && r.PrSizeLabelState.Count > 0).ToList();
    }
}
