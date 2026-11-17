using MintPlayer.Spark.Cli;

namespace MintPlayer.Spark.Cli.Tests;

public class SecurityRightsTests
{
    private const string Security = """
        {
          "groups": {
            "00000000-0000-0000-0000-000000000001": "Signed-in users",
            "00000000-0000-4000-8000-000000000010": "Administrators"
          },
          "rights": [
            { "key": "people", "resource": "QueryReadEditNewDelete/Person", "groupId": "@anonymous" }
          ]
        }
        """;

    [Theory]
    [InlineData("Administrators", "00000000-0000-4000-8000-000000000010")]
    [InlineData("administrators", "00000000-0000-4000-8000-000000000010")]
    [InlineData("@authenticated", "@authenticated")]
    [InlineData("00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000001")]
    public void ResolveGroup_maps_a_name_to_its_id(string group, string expected)
    {
        SecurityRights.ResolveGroup(Security, group).Should().Be(expected);
    }

    [Fact]
    public void ResolveGroup_refuses_an_unknown_group()
    {
        var act = () => SecurityRights.ResolveGroup(Security, "Editors");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Editors*Administrators*");
    }

    [Fact]
    public void Add_appends_the_right()
    {
        var result = SecurityRights.Add(Security, "invoice", "QueryReadEditNewDelete/Invoice", "00000000-0000-4000-8000-000000000010");

        result.Should().Contain("""
                { "key": "people", "resource": "QueryReadEditNewDelete/Person", "groupId": "@anonymous" },
                { "key": "invoice", "resource": "QueryReadEditNewDelete/Invoice", "groupId": "00000000-0000-4000-8000-000000000010" }
            """);
    }

    [Fact]
    public void Add_refuses_a_key_that_is_taken()
    {
        var act = () => SecurityRights.Add(Security, "people", "QueryRead/Person", "@anonymous");

        act.Should().Throw<InvalidOperationException>().WithMessage("*people*");
    }
}
