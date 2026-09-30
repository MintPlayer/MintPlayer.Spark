using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #460 contributions, Q2 — the reserved action verbs read by reflection from
/// <c>[assembly: SparkReservedActions(typeof(X))]</c>, and the startup check that refuses a custom
/// action named like one.
/// </summary>
public class SparkReservedActionRegistryTests
{
    private static readonly IReadOnlyList<SparkReservedAction> Reserved = SparkReservedActionRegistry.Discover(
    [
        typeof(SparkCoreActions).Assembly,       // core + combined
        typeof(SparkSoftDeleteOptions).Assembly,        // SoftDeleteRights, declared by the SoftDelete package
        typeof(HistoryRights).Assembly,
        typeof(ModerationRights).Assembly,
    ]);

    [Fact]
    public void Each_package_declares_its_own_verbs()
    {
        Verbs(typeof(SparkCoreActions)).Should().BeEquivalentTo(["Query", "Read", "New", "Edit", "Delete", "Replicate"]);
        Verbs(typeof(SparkCombinedActions)).Should().BeEquivalentTo(SparkCombinedActions.Names);
        Verbs(typeof(SoftDeleteRights)).Should().BeEquivalentTo(["Restore", "Purge", "ViewDeleted"]);
        Verbs(typeof(HistoryRights)).Should().BeEquivalentTo(["History", "Revert"]);
        // ModerationRights.Target names a pseudo-type, not a verb, and is marked [SparkNotAnAction].
        Verbs(typeof(ModerationRights)).Should().BeEquivalentTo(["Vote", "Downvote", "Flag", "Lock", "Review", "Suspend", "Audit"]);
    }

    [Fact]
    public void The_owning_assembly_is_the_one_carrying_the_attribute()
    {
        Reserved.Single(r => r.Verb == SoftDeleteRights.Restore).Assembly.Should().BeSameAs(typeof(SparkSoftDeleteOptions).Assembly,
            "SoftDelete.Abstractions references no Spark package, so the SoftDelete package declares its verbs");
    }

    [Fact]
    public void Every_verb_Moderation_names_as_a_literal_is_a_reserved_verb()
    {
        // ModerationRights cannot reference SoftDelete or History, so it spells their verbs; this holds
        // the spelling to their declarations.
        var verbs = Reserved.Select(r => r.Verb).ToHashSet(StringComparer.Ordinal);
        foreach (var action in ModerationRights.NeverEarnable.Concat(ModerationRights.DefaultEarnable))
            verbs.Should().Contain(action);
    }

    [Fact]
    public void The_process_wide_registry_sees_the_verbs_of_referenced_packages()
    {
        SparkReservedActionRegistry.IsReserved("restore").Should().BeTrue();
        SparkReservedActionRegistry.IsReserved("Revert").Should().BeTrue();
        SparkReservedActionRegistry.IsReserved("QueryReadEditNewDelete").Should().BeTrue();
        SparkReservedActionRegistry.IsReserved("Moderation").Should().BeFalse();
        SparkReservedActionRegistry.IsReserved("CarCopy").Should().BeFalse();
    }

    [Theory]
    [InlineData("Restore")]
    [InlineData("restore")]
    [InlineData("LOCK")]
    public void A_custom_action_named_like_a_reserved_verb_refuses_startup(string name)
    {
        var check = () => SparkReservedActionRegistry.EnsureNoCollisions([(name, typeof(FixtureCollidingAction))], Reserved);

        var message = check.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain($"'{name}'");
        message.Should().Contain(typeof(FixtureCollidingAction).FullName!);
        var owner = Reserved.First(r => string.Equals(r.Verb, name, StringComparison.OrdinalIgnoreCase));
        message.Should().Contain($"'{owner.Verb}'");
        message.Should().Contain(owner.Assembly.GetName().Name!);
    }

    [Fact]
    public void Custom_actions_with_their_own_names_start()
    {
        var check = () => SparkReservedActionRegistry.EnsureNoCollisions(
            [("CarCopy", typeof(FixtureCollidingAction)), ("Editor", typeof(FixtureCollidingAction)), ("Moderation", typeof(FixtureCollidingAction))],
            Reserved);

        check.Should().NotThrow();
    }

    [Fact]
    public void The_moderation_init_report_renders_every_reserved_content_verb_per_type()
    {
        var options = new SparkModerationOptions();
        options.Privileges["Curators"] = new() { GroupId = Guid.Parse("00000000-0000-0000-0000-00000000c001"), Grants = ["History", "QueryRead"] };

        var report = SparkModerationInitExtensions.Render(options, ["MoPost"]);

        report.Should().Contain("\"resource\": \"History/MoPost\"").And.Contain("\"resource\": \"QueryRead/MoPost\"");
        report.Should().Contain("\"resource\": \"Restore/MoPost\"").And.Contain("\"resource\": \"Revert/MoPost\"");
        report.Should().Contain("\"resource\": \"Audit/Moderation\"");
    }

    private static IEnumerable<string> Verbs(Type declaringType)
        => Reserved.Where(r => r.DeclaringType == declaringType).Select(r => r.Verb);

    /// <summary>Not an ICustomAction: the check is handed names and types, so no real colliding action exists to break other hosts.</summary>
    private sealed class FixtureCollidingAction;
}
