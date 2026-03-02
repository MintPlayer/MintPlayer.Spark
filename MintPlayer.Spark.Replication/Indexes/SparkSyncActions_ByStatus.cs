using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Replication.Indexes;

public class SparkSyncActions_ByStatus : AbstractIndexCreationTask<SparkSyncAction>
{
    public SparkSyncActions_ByStatus()
    {
        Map = actions => from action in actions
            select new
            {
                action.Status,
                action.OwnerModuleName,
                action.Collection,
                action.CreatedAtUtc
            };
    }
}
