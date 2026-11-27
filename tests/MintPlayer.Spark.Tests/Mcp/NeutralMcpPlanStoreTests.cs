using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Mcp.Plans;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// #536: MCP plans on the database <c>SPARK_TEST_DATABASE</c> picks. <c>AddMcp()</c> registers the plans' table (D36),
/// and a plan whose <see cref="McpPlan.ExpiresAt"/> just passed still loads, because the store's own expiry trails it by
/// <see cref="McpPlan.HousekeepingGrace"/>: the planner can tell the agent the plan expired.
/// </summary>
public class NeutralMcpPlanStoreTests : SparkTestDriver
{
    public sealed class McpContext : SparkContext;

    [Fact]
    public async Task A_just_expired_plan_still_loads()
    {
        await using var host = new SparkEndpointFactory<McpContext>(Database, [], configureSpark: spark => spark.AddMcp());
        var store = host.GetService<ISparkStore>();
        var id = "mcpplans/" + Guid.NewGuid().ToString("N");
        var plan = new McpPlan
        {
            Id = Id<McpPlan>.FromTrusted(id),
            UserId = "users/ann",
            AgentKind = "mcp",
            CreatedAt = DateTime.UtcNow.AddMinutes(-11),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        };
        plan.Stamp();
        using (var session = store.OpenSession())
        {
            await session.StoreAsync(plan, id);
            await session.SaveChangesAsync();
        }

        using var read = store.OpenSession();
        var loaded = await read.LoadAsync<McpPlan>(id);
        loaded.Should().NotBeNull("the store deletes it a day later, not when it stops being appliable");
        loaded!.ExpiresAt.Should().BeBefore(DateTime.UtcNow);
        loaded.ExpiresAtUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(23));
    }
}
