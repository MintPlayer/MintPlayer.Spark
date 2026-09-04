using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Messaging.Indexes;

/// <summary>
/// Backs the lane drain: "the actionable messages of this lane, oldest first".
/// </summary>
/// <remarks>
/// <see cref="SparkMessage.Sequence"/> is indexed because it is the ordering key. The obvious
/// alternative — order by <c>CreatedAtUtc</c> then by the document id — is wrong: it compiles to
/// RavenDB's <c>order by id()</c>, a lexicographic sort over ids that are not zero-padded, so
/// <c>SparkMessages/10-A</c> sorts before <c>SparkMessages/2-A</c>.
/// </remarks>
public class SparkMessages_ByQueue : AbstractIndexCreationTask<SparkMessage>
{
    public SparkMessages_ByQueue()
    {
        Map = messages => from msg in messages
            select new
            {
                msg.QueueName,
                msg.PartitionKey,
                msg.Status,
                msg.NextAttemptAtUtc,
                msg.VisibleAtUtc,
                msg.Sequence,
                msg.CreatedAtUtc
            };
    }
}
