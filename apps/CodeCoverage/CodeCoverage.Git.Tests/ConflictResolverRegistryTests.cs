using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.Tests._Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// The resolver registry (docs/coverage_merge_queue_PRD.md §10.3, R1): the names the DI container serves are exactly the
/// names the parser accepts (D57: parser-accepts ⇒ writer-accepts), one name has one implementation, and the dispatch
/// through the registry builds the very tree the inline take-base resolver built before it.
/// </summary>
public class ConflictResolverRegistryTests
{
    private static ConflictResolverRegistry FromContainer()
        => new ServiceCollection().AddMergeQueueResolution().BuildServiceProvider().GetRequiredService<ConflictResolverRegistry>();

    [Fact]
    public void The_registered_strategies_and_validators_are_exactly_the_wire_rules_name_sets()
    {
        var registry = FromContainer();

        registry.StrategyNames.Order(StringComparer.Ordinal).Should().Equal(MergeQueueWireRules.Strategies.Order(StringComparer.Ordinal));
        registry.ValidatorNames.Order(StringComparer.Ordinal).Should().Equal(MergeQueueWireRules.Validators.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_built_in_set_equals_the_registered_set()
    {
        var registry = FromContainer();

        ConflictResolverRegistry.Default.StrategyNames.Order(StringComparer.Ordinal).Should().Equal(registry.StrategyNames.Order(StringComparer.Ordinal));
        ConflictResolverRegistry.Default.ValidatorNames.Order(StringComparer.Ordinal).Should().Equal(registry.ValidatorNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Two_resolvers_with_one_name_throw()
    {
        FluentActionsFor.Act(() => new ConflictResolverRegistry([new TakeBaseResolver(), new TakeBaseResolver()], []))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_unknown_strategy_throws()
    {
        FluentActionsFor.Act(() => ConflictResolverRegistry.Default.Resolver("merge-driver")).Should().Throw<KeyNotFoundException>();
    }

    /// <summary>
    /// The golden test: the fixture of <c>MergeQueueResolverTests.Two_entities_added_in_different_gaps_…</c>, resolved
    /// through the registry, gives the tree the pre-registry resolver built: merge-tree's tree with the hash file replaced
    /// by the base branch's blob, which here is main's own tree. Read with the git CLI alone, independent of the code under test.
    /// </summary>
    [Fact]
    public async Task Dispatching_through_the_registry_builds_the_same_tree_as_inline_take_base()
    {
        using var repo = new TestRepo();
        string[] entities = ["Address", "Motorcycle", "Person"];
        repo.Write(ModelHashes.Path, ModelHashes.Render("base", entities));
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("with-customer", [.. entities, "Customer"]));
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("with-order", [.. entities, "Order"]));
        repo.Commit("B");
        var bare = repo.Bare();

        // Both sides changed the hash file alone, so take-base's tree is main's own tree.
        var expected = TestRepo.Git(bare, "rev-parse", "main^{tree}").Trim();

        var outcome = await new MergeQueueResolver(TestRepo.Runner(), FromContainer())
            .MergeAsync(bare, "main", "pr", AutoResolveRule.SparkGeneratedFiles, CancellationToken.None);

        var resolved = outcome.Should().BeOfType<MergeOutcome.Resolved>().Which;
        resolved.Tree.Should().Be(expected);
        resolved.Resolutions.Should().Equal([new ResolvedPath(ModelHashes.Path, MergeQueueWireRules.TakeBase, true, 0)]);
    }

    [Fact]
    public async Task An_evict_rule_masks_a_later_rule_for_its_path()
    {
        using var repo = new TestRepo();
        repo.Write(ModelHashes.Path, ModelHashes.Render("base", ["Address"]));
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("main", ["Address", "Customer"]));
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("pr", ["Address", "Order"]));
        repo.Commit("B");
        var bare = repo.Bare();

        var outcome = await new MergeQueueResolver(TestRepo.Runner()).MergeAsync(bare, "main", "pr",
            [AutoResolveRule.Create("**/modelHashes.json", MergeQueueWireRules.Evict), .. AutoResolveRule.SparkGeneratedFiles], CancellationToken.None);

        var evicted = outcome.Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.ConflictOutsideAllowList);
        evicted.Paths.Should().Equal([ModelHashes.Path]);
    }
}
