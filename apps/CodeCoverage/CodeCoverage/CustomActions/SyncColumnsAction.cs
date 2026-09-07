using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// Re-reads a board's Status field from GitHub and replaces its cached columns.
/// <para>
/// This exists because <b>nothing tells this app when a column changes.</b> The Projects V2
/// webhook events are organization-scoped — measured: a user-account installation receives none of
/// them — and, more decisively, <b>renaming a Status option produces no event for either owner
/// type</b>. So the cached columns can only be corrected by the nightly reconciliation or by this
/// button, and a rule whose target column was deleted stays syntactically valid and silently never
/// fires until one of the two runs.
/// </para>
/// <para>
/// Operates on the board whose page it is on, through <c>args.Parent</c>, not on selected rows —
/// hence <c>selectionRule "=0"</c> in <c>customActions.json</c>. It is offered on
/// <c>GitHubProject</c> because <c>SyncColumns/GitHubProject</c> is the only grant: actions attach
/// by <b>right</b>, not by declaration, and that file is evaluated against every type.
/// </para>
/// <para>
/// Every exit says what happened. A silent <c>return</c> is indistinguishable from success in the
/// browser, which is exactly how a dead queue read as a permission problem for months in this
/// application's history.
/// </para>
/// </summary>
public partial class SyncColumnsAction : SparkCustomAction
{
    [Inject] private readonly IInstallationProjects installationProjects;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<SyncColumnsAction> logger;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent is not { } page || string.IsNullOrEmpty(page.Id))
        {
            manager.Client.Notify(
                "No board in context, so there is nothing to synchronize.", NotificationKind.Warning);
            return;
        }

        var board = await session.LoadAsync<GitHubProject>(page.Id, cancellationToken);
        if (board is null)
        {
            manager.Client.Notify("That board no longer exists.", NotificationKind.Error);
            return;
        }

        // Row security has already filtered what the caller can load, so reaching here means the
        // caller manages this owner. Reachability is a separate question: a disconnected board may
        // have no usable installation token, and calling GitHub would fail with a less informative
        // error than this one.
        if (board.Connection == RepositoryConnection.Disconnected)
        {
            manager.Client.Notify(
                $"Board #{board.Number} is disconnected ({board.DisconnectedReason ?? "reason unrecorded"}), "
                + "so its columns cannot be read. Reconnect the GitHub App to this owner first.",
                NotificationKind.Warning);
            return;
        }

        ProjectStatusField statusField;
        try
        {
            statusField = await installationProjects.GetStatusFieldAsync(
                board.InstallationId, board.NodeId, cancellationToken);
        }
        catch (Exception ex)
        {
            // The cached columns are deliberately left alone. Stale columns are usable; empty ones
            // would make every rule on this board report a missing target.
            logger.LogWarning(ex, "Could not read the Status field for board #{Number}", board.Number);
            manager.Client.Notify(
                $"GitHub could not be reached for board #{board.Number}. The previously cached columns "
                + "are unchanged; try again shortly.",
                NotificationKind.Error);
            return;
        }

        if (!statusField.Exists)
        {
            // A legitimate state, not an error — but automation has nothing to target, and leaving
            // stale columns would suggest otherwise.
            board.StatusFieldId = null;
            board.Columns = [];
            board.ColumnsSyncedAtUtc = DateTime.UtcNow;
            await session.SaveChangesAsync(cancellationToken);

            manager.Client.RefreshAttribute(page, nameof(GitHubProject.Columns));
            manager.Client.Notify(
                $"Board #{board.Number} has no Status field, so there are no columns to move cards between. "
                + "Add one on GitHub, then synchronize again.",
                NotificationKind.Warning);
            return;
        }

        board.StatusFieldId = statusField.FieldId;
        board.Columns = statusField.Columns
            .Select(c => new ProjectColumn { Id = c.OptionId, Name = c.Name })
            .ToList();
        board.ColumnsSyncedAtUtc = DateTime.UtcNow;

        // Rules pointing at options that have gone are reported, never deleted. The rules are the
        // user's, and dropping one silently would destroy configuration and hide the reason their
        // automation stopped. The message names the events so they can be re-pointed.
        var orphaned = board.EventMappings
            .Where(m => m.Enabled && !string.IsNullOrEmpty(m.TargetColumnOptionId))
            .Where(m => board.Columns.All(c => c.Id != m.TargetColumnOptionId))
            .ToList();

        await session.SaveChangesAsync(cancellationToken);

        manager.Client.RefreshAttribute(page, nameof(GitHubProject.Columns));
        manager.Client.RefreshAttribute(page, nameof(GitHubProject.EventMappings));
        manager.Client.RefreshAttribute(page, nameof(GitHubProject.ColumnsSyncedAtUtc));

        if (orphaned.Count > 0)
        {
            manager.Client.Notify(
                $"Synchronized {board.Columns.Count} column(s), but {orphaned.Count} enabled rule(s) now "
                + $"target a column that no longer exists ({string.Join(", ", orphaned.Select(m => m.EventType))}). "
                + "Re-pick their target column.",
                NotificationKind.Warning);
            return;
        }

        manager.Client.Notify(
            $"Synchronized {board.Columns.Count} column(s) for board #{board.Number}.",
            NotificationKind.Success);
    }
}
