using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Messaging.Indexes;

public class SparkMessages_ByQueue : AbstractIndexCreationTask<SparkMessage>
{
    public SparkMessages_ByQueue()
    {
        Map = messages => from msg in messages
            select new
            {
                msg.QueueName,
                msg.Status,
                msg.NextAttemptAtUtc,
                msg.CreatedAtUtc,
                // Indexed so MessageRetrySweeper can find abandoned claims — messages left at
                // Processing by a host that died mid-handler — without scanning the collection.
                msg.ClaimExpiresAtUtc,
                msg.WakeUp
            };
    }
}
