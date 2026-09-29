using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Messaging.Indexes;

/// <summary>
/// The single feeder's sorted view of the queue (#460, M16b): every message that may be claimed
/// <b>now</b>, ordered by <c>Priority desc, Sequence asc</c>. On each subscription wake-up the feeder
/// takes one page of this (<c>FeederBatchSize</c>) and serves it together with the subscription batch
/// itself, so a message the index has not caught up with yet is served from the batch instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claimable</b> is the subscription's own predicate, evaluated at index time so that the index
/// holds only the "pending now" set: a claimed, deferred, delayed, finished or dead-lettered message
/// has no entry. It deliberately contains no time comparison — deferral and delay are left as field
/// state (<c>NextAttemptAtUtc</c> set, <c>WakeUp</c> not true) until the sweeper wakes the message,
/// exactly as for the subscription. <c>WakeUp == true</c>, never <c>!= false</c>-style negations: an
/// absent field does not match <c>== false</c>. No <c>.HasValue</c> (a 500 on a static index).
/// </para>
/// <para>
/// <b>Sequence</b> is server-assigned (spike S-M7): the captured <see cref="SparkMessage.QueuedAtUtc"/>,
/// or until the feeder has captured it, the document's own <c>@last-modified</c>, which RavenDB sets
/// when it commits the write. Never the producer's clock.
/// </para>
/// <para>
/// A hand-written <see cref="AbstractIndexCreationTask{TDocument, TReduceResult}"/>, like the other
/// messaging and moderation indexes: <c>SparkIndexCreationTask</c> only applies generated
/// <c>[Search]</c>/<c>DateTimeOffset</c> field options, of which this has none (so SPARK018 does not
/// apply), and deriving from it would make Messaging depend on the core <c>MintPlayer.Spark</c>
/// package for an empty override. Deployed next to <see cref="SparkMessages_ByQueue"/> by
/// <c>CreateSparkMessagingIndexes</c>.
/// </para>
/// </remarks>
public class SparkMessages_ByPriority : AbstractIndexCreationTask<SparkMessage, SparkMessages_ByPriority.Entry>
{
    /// <summary>One claimable message. Stored, so the feeder's page is read from the index alone.</summary>
    public sealed class Entry
    {
        public string? Id { get; set; }
        public string QueueName { get; set; } = string.Empty;
        public int Priority { get; set; }
        public DateTime Sequence { get; set; }
    }

    public SparkMessages_ByPriority()
    {
        Map = messages => from m in messages
            where (m.Status == EMessageStatus.Pending && (m.NextAttemptAtUtc == null || m.WakeUp == true))
                || (m.Status == EMessageStatus.Failed && m.WakeUp == true)
            select new Entry
            {
                QueueName = m.QueueName,
                // Coalesced: a message written before M16b has no Priority member, and a null sorts
                // below Low under "desc" (measured, S-M7), where it should read as Normal.
                Priority = (int?)m.Priority ?? 0,
                Sequence = m.QueuedAtUtc != null
                    ? m.QueuedAtUtc.Value
                    : MetadataFor(m).Value<DateTime>("@last-modified"),
            };

        Store(x => x.QueueName, FieldStorage.Yes);
        Store(x => x.Priority, FieldStorage.Yes);
        Store(x => x.Sequence, FieldStorage.Yes);
    }
}
