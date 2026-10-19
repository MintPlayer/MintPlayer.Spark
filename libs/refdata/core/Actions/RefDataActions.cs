using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.ReferenceData.Import;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.ReferenceData.Actions;

// The admin pages (PRD §4.16, plan R4 "Admin pages through the generic Spark UI"): one query per reference type. The
// types are read-only (no New/Edit/Delete right ships in App_Data/security.json): imports and the seed write them.
// Countries, places and postal codes are declared by MintPlayer.Spark.ReferenceData.Client's layer (the consumer's
// read-only local copies, plan R10), which this package references, so a host never composes them twice.

public partial class RefStreetActions : DefaultPersistentObjectActions<RefStreet>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<RefStreet> RefStreets() => session.Query<RefStreet>();
}

public partial class RefLineageEventActions : DefaultPersistentObjectActions<RefLineageEvent>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<RefLineageEvent> RefLineageEvents() => session.Query<RefLineageEvent>();
}

public partial class RefImportRunActions : DefaultPersistentObjectActions<RefImportRun>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<RefImportRun> RefImportRuns() => session.Query<RefImportRun>();
}

/// <summary>
/// "Import now" (PRD §4.5 step 3): queues the same message a changed source queues. The run fetches every enabled
/// source's current version; a run of unchanged versions writes nothing (RS2).
/// </summary>
internal sealed partial class RefDataImportNowAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IMessageBus bus;
    [Inject] private readonly TimeProvider time;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var key = "admin/" + time.GetUtcNow().ToUnixTimeSeconds();
        await bus.BroadcastOnceAsync(new RefDataImportMessage { Trigger = "admin", Key = key }, key, cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage("referenceData.importQueued"), NotificationKind.Success);
    }
}
