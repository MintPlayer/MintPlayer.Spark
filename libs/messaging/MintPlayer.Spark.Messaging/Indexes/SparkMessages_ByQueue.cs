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
                msg.CreatedAtUtc
            };
    }
}
