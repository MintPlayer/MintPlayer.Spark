using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal class SparkAuthGroup : IEndpointGroup
{
    public static string Prefix => "/spark/auth";
}

/// <summary>
/// <c>/spark/auth/manage</c>: the signed-in user's account pages. The group carries the authorization
/// requirement once; it is never switched off as a whole — each endpoint whose existence depends on the
/// <see cref="Configuration.SparkLocalCredentials"/> mode says so in its own <c>IsEnabled</c> (D5).
/// </summary>
[MemberOf<SparkAuthGroup>]
internal class SparkAuthManageGroup : IEndpointGroup
{
    public static string Prefix => "/manage";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.RequireAuthorization();
}
