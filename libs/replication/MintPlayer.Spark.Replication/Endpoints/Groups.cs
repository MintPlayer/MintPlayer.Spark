using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Replication.Endpoints;

internal class SparkEtlGroup : IEndpointGroup
{
    public static string Prefix => "/spark/etl";
}

internal class SparkSyncGroup : IEndpointGroup
{
    public static string Prefix => "/spark/sync";
}
