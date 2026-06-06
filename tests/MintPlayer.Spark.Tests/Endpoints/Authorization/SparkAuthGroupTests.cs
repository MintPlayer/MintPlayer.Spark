using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Endpoints;

namespace MintPlayer.Spark.Tests.Endpoints.Authorization;

public class SparkAuthGroupTests
{
    [Fact]
    public void Prefix_is_spark_auth()
    {
        SparkAuthGroup.Prefix.Should().Be("/spark/auth");
    }

    [Fact]
    public void All_auth_endpoints_are_members_of_SparkAuthGroup()
    {
        typeof(GetCurrentUser).Should().BeAssignableTo<IMemberOf<SparkAuthGroup>>();
        typeof(Logout).Should().BeAssignableTo<IMemberOf<SparkAuthGroup>>();
        typeof(CsrfRefresh).Should().BeAssignableTo<IMemberOf<SparkAuthGroup>>();
    }

    [Fact]
    public void Endpoint_paths_match_the_documented_routes()
    {
        GetCurrentUser.Path.Should().Be("/me");
        Logout.Path.Should().Be("/logout");
        CsrfRefresh.Path.Should().Be("/csrf-refresh");
    }
}
