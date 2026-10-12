using NSubstitute;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Tests.Builder;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// MCP PRD D6: a type's <c>agentPermissions</c> in its model JSON — one flags enum, written as
/// <c>"Read, Write"</c>, kept by model synchronization (which never deletes what a model states), shown by
/// <c>--spark-describe model</c>, and refused at startup as <c>Write</c> without <c>Read</c>.
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class AgentPermissionsModelTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static EntityTypeDefinition Type(string name, EAgentPermission permissions) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        AgentPermissions = permissions,
    };

    [Fact]
    public void Serializes_as_comma_separated_names_and_is_omitted_when_None()
    {
        var exposed = JsonNode.Parse(JsonSerializer.Serialize(Type("Contract", EAgentPermission.Read | EAgentPermission.Write), Web))!;
        exposed["agentPermissions"]!.GetValue<string>().Should().Be("Read, Write");

        var hidden = JsonNode.Parse(JsonSerializer.Serialize(Type("Secret", EAgentPermission.None), Web))!.AsObject();
        hidden.ContainsKey("agentPermissions").Should().BeFalse("the default adds no noise to a model file");

        JsonSerializer.Deserialize<EntityTypeDefinition>("""{ "id": "11111111-1111-1111-1111-111111111111", "name": "Contract", "agentPermissions": "Read, Write" }""", Web)!
            .AgentPermissions.Should().Be(EAgentPermission.Read | EAgentPermission.Write);
    }

    [Fact]
    public void Write_without_Read_is_refused()
    {
        var problems = AgentPermissionsCheck.Problems([Type("Contract", EAgentPermission.Write)]);

        problems.Should().ContainSingle().Which.Should().Contain("Contract").And.Contain("without \"Read\"");
        var act = () => AgentPermissionsCheck.Verify(LoaderOf(Type("Contract", EAgentPermission.Write)));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void None_Read_and_Read_Write_are_valid()
        => AgentPermissionsCheck.Problems(
        [
            Type("A", EAgentPermission.None),
            Type("B", EAgentPermission.Read),
            Type("C", EAgentPermission.Read | EAgentPermission.Write),
        ]).Should().BeEmpty();

    [Fact]
    public void A_value_naming_no_flag_is_refused()
        => AgentPermissionsCheck.Problems([Type("Odd", (EAgentPermission)5)]).Should().ContainSingle();

    [Fact]
    public void Describe_states_the_permission_even_when_absent()
    {
        MintPlayer.Spark.SparkDescribeCommand.AgentPermissionsOf("""{ "persistentObject": { "name": "C", "agentPermissions": "Read, Write" } }""")
            .Should().Be("Read, Write");
        MintPlayer.Spark.SparkDescribeCommand.AgentPermissionsOf("""{ "persistentObject": { "name": "C" } }""")
            .Should().Be("None (not exposed to agents)");
    }

    [Fact]
    public void Model_synchronization_keeps_a_hand_written_agentPermissions()
    {
        using var scratch = new ScratchContentRoot("spark-agentpermissions-sync-");
        scratch.Synchronize<ApSyncContext>();
        var file = Path.Combine(scratch.ModelPath, "ApSyncProbe.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["persistentObject"]!.AsObject().ContainsKey("agentPermissions").Should().BeFalse("nothing is generated: a type is not exposed by default");

        json["persistentObject"]!["agentPermissions"] = "Read, Write";
        File.WriteAllText(file, json.ToJsonString());
        scratch.Synchronize<ApSyncContext>();

        JsonNode.Parse(File.ReadAllText(file))!["persistentObject"]!["agentPermissions"]!.GetValue<string>().Should().Be("Read, Write");
    }

    private static IModelLoader LoaderOf(params EntityTypeDefinition[] definitions)
    {
        var loader = NSubstitute.Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(definitions);
        return loader;
    }

    // Private and nested: a model type visible to assembly-wide scans would leak into other fixtures.
    private sealed class ApSyncProbe
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ApSyncContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<ApSyncProbe> Probes => Session.Query<ApSyncProbe>();
    }
}
