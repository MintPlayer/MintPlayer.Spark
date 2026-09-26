using System.Reflection;
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
        // Membership is an attribute since Endpoints 11.1.0-rc.0, not an interface, and it is
        // inherited - so this asks the same question the generator does.
        typeof(GetCurrentUser).GetCustomAttribute<MemberOfAttribute<SparkAuthGroup>>(inherit: true).Should().NotBeNull();
        typeof(Logout).GetCustomAttribute<MemberOfAttribute<SparkAuthGroup>>(inherit: true).Should().NotBeNull();
        typeof(CsrfRefresh).GetCustomAttribute<MemberOfAttribute<SparkAuthGroup>>(inherit: true).Should().NotBeNull();
    }

    [Fact]
    public void Endpoint_paths_match_the_documented_routes()
    {
        GetCurrentUser.Path.Should().Be("/me");
        Logout.Path.Should().Be("/logout");
        CsrfRefresh.Path.Should().Be("/csrf-refresh");
    }
}
