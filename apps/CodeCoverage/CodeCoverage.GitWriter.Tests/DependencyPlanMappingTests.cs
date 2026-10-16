using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs.Dependencies;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The validators read the contract plan (D-V8 resolved): <see cref="DependencyPlanEntry"/> and
/// <see cref="DependencyPatchRequest"/> map from the REAL N3 plan and result
/// (<c>contracts/dependency-update/fixtures/valid/{plan,result}.npm-three-groups.json</c>).
/// </summary>
public class DependencyPlanMappingTests
{
    private static string Contract(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MintPlayer.Spark.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "apps", "CodeCoverage", "contracts", "dependency-update", "fixtures", "valid", name));
    }

    [Fact]
    public void Every_plan_item_becomes_an_entry_of_its_group_in_plan_order()
    {
        var plan = DependencyContract.Deserialize<DependencyPlan>(Contract("plan.npm-three-groups.json"))!;

        var entries = DependencyPlanEntry.From(plan);

        entries.Select(e => (e.GroupName, e.DepName, e.CurrentValue, e.NewValue)).Should().Equal(
        [
            ("express", "express", "^4.18.2", "^4.19.2"),
            ("react-dom", "react-dom", "17.0.2", "18.2.0"),
            ("chalk", "chalk", "4.1.0", "4.1.2"),
        ]);
        entries.Should().OnlyContain(e => e.PackageFile == "package.json" && e.Datasource == "npm" && e.Manager == "npm" && !e.IsLockfileOnly);
    }

    [Fact]
    public void The_claimed_groups_are_the_ones_result_json_says_it_applied()
    {
        var plan = DependencyContract.Deserialize<DependencyPlan>(Contract("plan.npm-three-groups.json"))!;
        var result = DependencyContract.Deserialize<ApplyResult>(Contract("result.npm-three-groups.json"))!;

        var request = DependencyPatchRequest.From(plan, result);

        request.Plan.Should().HaveCount(3);
        request.ClaimedAppliedGroups.Should().BeEquivalentTo(["express", "chalk"], because: "react-dom was unresolvable (blocker react)");
        DependencyPatchRequest.From(plan).ClaimedAppliedGroups.Should().BeNull();
    }

    [Fact]
    public void A_platform_group_contributes_no_entries()
    {
        var plan = DependencyContract.Deserialize<DependencyPlan>(Contract("plan.platform-dotnet.json"))!;

        DependencyPlanEntry.From(plan).Should().BeEmpty();
    }
}
