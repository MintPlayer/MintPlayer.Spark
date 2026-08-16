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
                action.CreatedAtUtc,
                // Both fields exist for SyncActionRetrySweeper, which needs to find actions whose
                // backoff has elapsed. Deliberately indexed rather than left to an auto-index: the
                // sweeper runs on a timer against a collection that can be large, and this is the
                // only query it makes.
                action.NextAttemptAtUtc,
                action.WakeUp
            };
    }
}
