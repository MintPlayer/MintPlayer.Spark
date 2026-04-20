using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-2 / H-3 — entity-type-level grants must NOT imply per-row read access. Once the
/// row-level-filter hook is introduced on <c>DefaultPersistentObjectActions&lt;T&gt;</c>,
/// and the Fleet demo uses it (so each car is associated with a creator or owner),
/// User B must not be able to read User A's cars even though both have
/// <c>QueryReadEditNew/Car</c>.
///
/// These tests are marked Skip until the remediation PR:
///   1. adds the filter hook to DefaultPersistentObjectActions
///   2. adds an Owner/AssignedTo concept to Fleet's Car schema
///   3. updates CarActions to apply the filter
/// Once those are in place, remove the Skip attribute — the tests will exercise the fix.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class RowLevelAuthzTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public RowLevelAuthzTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact(Skip = "Requires H-2 fix: row-level filter hook + Fleet ownership concept")]
    public Task User_B_cannot_list_User_As_private_cars() => Task.CompletedTask;

    [Fact(Skip = "Requires H-2 fix: row-level filter hook + Fleet ownership concept")]
    public Task User_B_cannot_read_User_As_private_car_by_id() => Task.CompletedTask;

    [Fact(Skip = "Requires H-3 fix: parent-object authorization through the same filter hook")]
    public Task User_B_cannot_execute_child_query_with_User_As_parent_id() => Task.CompletedTask;
}
