using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Migrations;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// <see cref="M_202610191530_AgentGrantTypesById"/> (D28, commerce X9, S22) rewrites each chosen type of a stored
/// agent grant from <c>{"Name":"Car"}</c> to <c>{"TypeId":"…"}</c>. The seeds reproduce the pre-D28 document exactly
/// (the account page and the identity provider's consent wrote <c>AgentGrantType.Name</c>), with the
/// <c>AgentGrants</c> collection set in metadata, so the migration runs against the shape a database holds.
/// </summary>
public class AgentGrantTypesByIdMigrationTests : SparkTestDriver
{
    private const string CarId = "27768be5-2ff5-4782-8b22-c0e8d163050e";
    private const string PersonId = "3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83";

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal) { ["Car"] = CarId, ["Person"] = PersonId };

    private sealed class LegacyType
    {
        public string Name { get; set; } = string.Empty;
        public EAgentPermission Permissions { get; set; } = EAgentPermission.Read;
    }

    private sealed class LegacyGrant
    {
        public string UserId { get; set; } = string.Empty;
        public EAgentKind Kind { get; set; }
        public string? ClientId { get; set; }
        public EAgentPermission Permissions { get; set; } = EAgentPermission.Read;
        public List<LegacyType>? Types { get; set; }
    }

    private async Task SeedLegacyAsync()
    {
        using var session = Store.OpenAsyncSession();
        async Task Put(string id, LegacyGrant grant)
        {
            await session.StoreAsync(grant, id);
            session.Advanced.GetMetadataFor(grant)["@collection"] = "AgentGrants";
        }

        await Put("AgentGrants/users/1/pat/t1", new LegacyGrant
        {
            UserId = "users/1", Kind = EAgentKind.PersonalAccessToken, ClientId = "t1", Permissions = EAgentPermission.Read | EAgentPermission.Write,
            Types = [new LegacyType { Name = "Car", Permissions = EAgentPermission.Read | EAgentPermission.Write }, new LegacyType { Name = "Person" }],
        });
        await Put("AgentGrants/users/1/app/agent", new LegacyGrant { UserId = "users/1", Kind = EAgentKind.MetadataDocument, ClientId = "agent", Types = null });
        await Put("AgentGrants/users/2/pat/t2", new LegacyGrant
        {
            UserId = "users/2", Kind = EAgentKind.PersonalAccessToken, ClientId = "t2",
            Types = [new LegacyType { Name = "Car" }, new LegacyType { Name = "Removed" }],
        });
        await session.SaveChangesAsync();
    }

    private async Task<JsonNode> RawAsync(string id)
    {
        using var reader = Store.OpenAsyncSession();
        var command = new Raven.Client.Documents.Commands.GetDocumentsCommand(Store.Conventions, id, includes: null, metadataOnly: false);
        await reader.Advanced.RequestExecutor.ExecuteAsync(command, reader.Advanced.Context);
        return JsonNode.Parse(command.Result.Results[0]!.ToString()!)!;
    }

    [Fact]
    public async Task Before_the_migration_a_name_grants_nothing()
    {
        await SeedLegacyAsync();

        using var session = Store.OpenAsyncSession();
        var grant = (await session.LoadAsync<AgentGrant>("AgentGrants/users/1/pat/t1"))!;
        grant.ToScope().For(CarId).Should().Be(EAgentPermission.None, "the pre-D28 shape names the type, and a grant is read by id now");
    }

    [Fact]
    public async Task Every_known_name_becomes_the_type_id_and_the_grant_applies_again()
    {
        await SeedLegacyAsync();

        var unknown = await AgentGrantTypeUpgrade.RunAsync(Store, Names);

        var raw = await RawAsync("AgentGrants/users/1/pat/t1");
        raw["Types"]![0]!["TypeId"]!.GetValue<string>().Should().Be(CarId);
        raw["Types"]![0]!["Name"].Should().BeNull();
        raw["Types"]![0]!["Permissions"].Should().NotBeNull("the permission is kept");
        raw["Types"]![1]!["TypeId"]!.GetValue<string>().Should().Be(PersonId);
        raw["Types"]![1]!["Name"].Should().BeNull("the name is dropped once the id is there");
        (await RawAsync("AgentGrants/users/1/app/agent"))["Types"].Should().BeNull("'everything' stays everything");

        using var session = Store.OpenAsyncSession();
        var grant = (await session.LoadAsync<AgentGrant>("AgentGrants/users/1/pat/t1"))!;
        grant.ToScope().For(CarId).Should().Be(EAgentPermission.Read | EAgentPermission.Write);
        grant.ToScope().For(PersonId).Should().Be(EAgentPermission.Read);

        var partly = await RawAsync("AgentGrants/users/2/pat/t2");
        partly["Types"]![0]!["TypeId"]!.GetValue<string>().Should().Be(CarId);
        partly["Types"]![1]!["Name"]!.GetValue<string>().Should().Be("Removed", "a name no type has is left as it is");
        partly["Types"]![1]!["TypeId"].Should().BeNull("so it grants nothing: fail closed");
        unknown.Should().Equal(("AgentGrants/users/2/pat/t2", "Removed"));
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await SeedLegacyAsync();
        await AgentGrantTypeUpgrade.RunAsync(Store, Names);
        var once = (await RawAsync("AgentGrants/users/1/pat/t1")).ToJsonString();

        await AgentGrantTypeUpgrade.RunAsync(Store, Names);

        (await RawAsync("AgentGrants/users/1/pat/t1")).ToJsonString().Should().Be(once);
    }

    [Fact]
    public async Task A_grant_written_by_the_new_code_is_left_as_it_is()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new AgentGrant
            {
                Id = "AgentGrants/users/3/pat/t3", UserId = "users/3", Kind = EAgentKind.PersonalAccessToken,
                Types = [new AgentGrantType { TypeId = CarId }],
            });
            await session.SaveChangesAsync();
        }
        var before = (await RawAsync("AgentGrants/users/3/pat/t3"))["Types"]!.ToJsonString();

        await AgentGrantTypeUpgrade.RunAsync(Store, Names);

        (await RawAsync("AgentGrants/users/3/pat/t3"))["Types"]!.ToJsonString().Should().Be(before);
    }
}
