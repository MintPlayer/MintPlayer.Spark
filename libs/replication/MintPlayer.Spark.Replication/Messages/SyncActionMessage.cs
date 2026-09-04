using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// One replication write, on its way to the module that owns the collection.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the <c>SparkSyncAction</c> document and its dedicated subscription. Sync actions
/// were always a message queue in disguise — the deleted worker carried its own wake-up gate, its own
/// sweeper (whose own comment admitted "two copies of this pattern is one more than ideal"), a
/// parallel status enum, a parallel retry engine and a parallel index, all doing what messaging
/// already does.
/// </para>
/// <para>
/// Folding them in is also what makes one subscription possible at all: a RavenDB subscription cannot
/// name two collections, and the alternative — one subscription over <c>@all_docs</c> filtered by
/// collection — was measured at 2934 ms to catch up where a single-collection subscription took 66 ms.
/// </para>
/// <para>
/// The lane is <c>spark-sync</c>, partitioned by document id, so writes to one document reach the
/// owner in order while writes to different documents do not wait for each other. That is what the
/// <c>BroadcastAsync(message, queueName)</c> overload was reaching for when it documented
/// "per-collection queue isolation (e.g. spark-sync-Cars)" — an overload which never worked, because
/// a queue name with no registered recipient never got a worker.
/// </para>
/// </remarks>
[MessageQueue(SyncActionMessage.LaneName)]
public sealed class SyncActionMessage
{
    public const string LaneName = "spark-sync";

    /// <summary>The module that owns the collection and will apply the action.</summary>
    public string OwnerModuleName { get; set; } = string.Empty;

    /// <summary>The module that made the write. Checked against the client certificate by the owner.</summary>
    public string RequestingModule { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;

    /// <summary>The ordering domain: writes to one document are applied in the order they were made.</summary>
    public string DocumentId { get; set; } = string.Empty;

    public List<SyncAction> Actions { get; set; } = [];
}
