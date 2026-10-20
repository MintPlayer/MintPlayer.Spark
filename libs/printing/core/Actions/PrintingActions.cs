using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Printing.Indexes;
using MintPlayer.Spark.Printing.Models;
using MintPlayer.Spark.Printing.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Printing.Actions;

/// <summary>Print jobs: read-only, listed through <c>Printing/Jobs</c> newest first; a reprint is the <c>PrintingReprint</c> action.</summary>
public partial class PrintJobActions : DefaultPersistentObjectActions<PrintJob>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<PrintJob> PrintJobs()
        => session.Query<PrintJobEntry>(PrintingIndexes.Jobs)
            .OrderByDescending(e => e.CreatedAt)
            .OfType<PrintJob>();
}

/// <summary>Agents' last heartbeats (Q-W14 monitoring): a small list, no index.</summary>
public partial class PrintAgentStatusActions : DefaultPersistentObjectActions<PrintAgentStatus>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<PrintAgentStatus> PrintAgentStatuses() => session.Query<PrintAgentStatus>();
}

/// <summary>Reprints the selected jobs, each as a new job on the same printer (PRD §4.11).</summary>
internal sealed partial class PrintingReprintAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IPrintJobs jobs;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.Parent?.Id is { Length: > 0 } parent
            ? [parent]
            : args.SelectedItems.Select(i => i.Id).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
        var queued = 0;
        foreach (var id in ids)
            if ((await jobs.ReprintAsync(id, currentUser.Id, cancellationToken)).Succeeded)
                queued++;
        manager.Client.Notify(manager.GetTranslatedMessage("printing.reprinted", queued), NotificationKind.Success);
    }
}
