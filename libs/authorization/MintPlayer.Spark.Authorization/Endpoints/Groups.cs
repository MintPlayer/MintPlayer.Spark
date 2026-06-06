using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal class SparkAuthGroup : IEndpointGroup
{
    public static string Prefix => "/spark/auth";
}
