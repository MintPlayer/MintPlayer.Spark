using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// The merge queue's resolver (docs/coverage_merge_queue_PRD.md §5.3, O1, O2) on real repositories: every merge here is
/// a real <c>git merge-tree</c> run over commits made with the git CLI. The base branch is <c>main</c>; the pull request
/// is <c>pr</c>. "Merged A" means <c>main</c> already moved past the point <c>pr</c> branched from.
/// </summary>
public class MergeQueueResolverTests
{
    private const string Translations = "apps/DemoApp/DemoApp/App_Data/translations.json";
    private static readonly IReadOnlyList<AutoResolveRule> Rules = [AutoResolveRule.SparkModelHashes];

    private static readonly string[] BaseEntities = ["Address", "Motorcycle", "Person"];

    /// <summary>The base commit on main: three entities, their model files and the hash file; <c>pr</c> branches here.</summary>
    private static TestRepo Seed()
    {
        var repo = new TestRepo();
        foreach (var entity in BaseEntities)
            repo.Write($"apps/DemoApp/DemoApp/App_Data/Model/{entity}.json", $"{{\n  \"name\": \"{entity}\"\n}}\n");
        repo.Write(ModelHashes.Path, ModelHashes.Render("base", BaseEntities));
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address\" }\n}\n");
        repo.Commit("base");
        repo.InWork("branch", "pr");
        return repo;
    }

    /// <summary>One side adds an entity the way synchronize does: a new model file, a new line in <c>files</c> and in
    /// <c>entities</c>, and new roll-ups.</summary>
    private static void AddEntity(TestRepo repo, string entity, string rollUp, IEnumerable<string>? existing = null)
    {
        var entities = (existing ?? BaseEntities).Append(entity).ToList();
        repo.Write($"apps/DemoApp/DemoApp/App_Data/Model/{entity}.json", $"{{\n  \"name\": \"{entity}\"\n}}\n");
        repo.Write(ModelHashes.Path, ModelHashes.Render(rollUp, entities));
    }

    private static Task<MergeOutcome> MergeAsync(string bare) => new MergeQueueResolver(TestRepo.Runner()).MergeAsync(bare, "main", "pr", Rules, CancellationToken.None);

    [Fact]
    public async Task Two_entities_added_in_different_gaps_conflict_only_on_the_roll_ups_and_resolve_to_the_base_side()
    {
        using var repo = Seed();
        AddEntity(repo, "Customer", "with-customer"); // between Address and Motorcycle
        repo.Commit("A: Customer");
        repo.Checkout("pr");
        AddEntity(repo, "Order", "with-order");       // between Motorcycle and Person
        repo.Commit("B: Order");
        var bare = repo.Bare();

        // Control: git itself reports the conflict, and only in the hash file.
        var (code, _, _) = TestRepo.TryGit(bare, "merge-tree", "--write-tree", "--name-only", "main", "pr");
        code.Should().Be(1);

        var outcome = await MergeAsync(bare);

        var resolved = outcome.Should().BeOfType<MergeOutcome.Resolved>().Which;
        resolved.Paths.Should().Equal([ModelHashes.Path]);
        resolved.BaseCommit.Should().Be(TestRepo.Git(bare, "rev-parse", "main").Trim());
        resolved.HeadCommit.Should().Be(TestRepo.Git(bare, "rev-parse", "pr").Trim());

        // take-base: the hash file is main's, byte for byte; both model files are there; no blob holds a marker.
        TestRepo.Git(bare, "rev-parse", $"{resolved.Tree}:{ModelHashes.Path}").Should().Be(TestRepo.Git(bare, "rev-parse", $"main:{ModelHashes.Path}"));
        var files = TestRepo.Git(bare, "ls-tree", "-r", "--name-only", resolved.Tree).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        files.Should().Contain("apps/DemoApp/DemoApp/App_Data/Model/Customer.json");
        files.Should().Contain("apps/DemoApp/DemoApp/App_Data/Model/Order.json");
        foreach (var file in files)
            TestRepo.Git(bare, "cat-file", "blob", $"{resolved.Tree}:{file}").Should().NotContain("<<<<<<<");
        // Against main, the squashed change is exactly the pull request's model file; the hashes are regenerated next.
        TestRepo.Git(bare, "diff-tree", "-r", "--name-status", "main", resolved.Tree).Trim()
            .Should().Be("A\tapps/DemoApp/DemoApp/App_Data/Model/Order.json");
    }

    [Fact]
    public async Task The_squash_commit_sits_on_the_base_with_the_authors_identity_and_the_bots_as_committer()
    {
        using var repo = Seed();
        AddEntity(repo, "Customer", "with-customer");
        repo.Commit("A: Customer");
        repo.Checkout("pr");
        AddEntity(repo, "Order", "with-order");
        repo.Commit("B: Order");
        var bare = repo.Bare();
        var resolver = new MergeQueueResolver(TestRepo.Runner());
        var resolved = (MergeOutcome.Resolved)await resolver.MergeAsync(bare, "main", "pr", Rules, CancellationToken.None);

        var when = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(2));
        var commit = await resolver.CommitAsync(bare, resolved.Tree, resolved.BaseCommit,
            new GitIdentity("Pull Request Author", "author@example.invalid", when),
            new GitIdentity("coverage-bot", "bot@example.invalid", when),
            "Add Order (#2)\n\nCo-authored-by: Someone <someone@example.invalid>\n", CancellationToken.None);

        TestRepo.Git(bare, "log", "-1", "--format=%P|%T|%an|%ae|%cn|%ce|%at|%ai", commit).Trim()
            .Should().Be($"{resolved.BaseCommit}|{resolved.Tree}|Pull Request Author|author@example.invalid|coverage-bot|bot@example.invalid|{when.ToUnixTimeSeconds()}|2026-10-14 12:00:00 +0200");
        TestRepo.Git(bare, "log", "-1", "--format=%B", commit).Should().StartWith("Add Order (#2)\n\nCo-authored-by: Someone");
    }

    [Fact]
    public async Task A_merge_without_conflicts_is_clean_and_keeps_merge_trees_tree()
    {
        using var repo = Seed();
        repo.Write("README.md", "A\n");
        repo.Commit("A: readme");
        repo.Checkout("pr");
        AddEntity(repo, "Order", "with-order");
        repo.Commit("B: Order");
        var bare = repo.Bare();

        var outcome = await MergeAsync(bare);

        var clean = outcome.Should().BeOfType<MergeOutcome.Clean>().Which;
        clean.Tree.Should().Be(TestRepo.Git(bare, "merge-tree", "--write-tree", "main", "pr").Trim());
    }

    [Fact]
    public async Task A_conflict_in_a_hand_authored_file_evicts_even_when_the_hash_file_alone_would_resolve()
    {
        using var repo = Seed();
        AddEntity(repo, "Customer", "with-customer");
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Customer\": { \"en\": \"Customer\" }\n}\n");
        repo.Commit("A: Customer");
        repo.Checkout("pr");
        AddEntity(repo, "Order", "with-order");
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Order\": { \"en\": \"Order\" }\n}\n");
        repo.Commit("B: Order");

        var outcome = await MergeAsync(repo.Bare());

        var evicted = outcome.Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.ConflictOutsideAllowList);
        evicted.Paths.Should().Equal([Translations]);
    }

    [Fact]
    public async Task A_conflict_on_a_per_file_hash_line_evicts()
    {
        using var repo = Seed();
        repo.Write(ModelHashes.Path, ModelHashes.Render("person-a", BaseEntities, new Dictionary<string, string> { ["Person"] = ModelHashes.Hash("edit", "a") }));
        repo.Commit("A: edit Person");
        repo.Checkout("pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("person-b", BaseEntities, new Dictionary<string, string> { ["Person"] = ModelHashes.Hash("edit", "b") }));
        repo.Commit("B: edit Person");

        var outcome = await MergeAsync(repo.Bare());

        var evicted = outcome.Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.HunkOutsideAllowedKeys);
        evicted.Paths.Should().Equal([ModelHashes.Path]);
    }

    [Fact]
    public async Task Two_entities_inserted_into_the_same_gap_resolve_under_the_O19_whole_file_rules()
    {
        // Customer and Dog both sort between Address and Motorcycle: the insertions touch the same lines of `files` and
        // `entities`, a conflict outside the roll-ups. O2's key rule evicts it (the control); O19 makes the wholly generated
        // hash file resolvable on any line, and the regenerate step rewrites it.
        using var repo = Seed();
        AddEntity(repo, "Customer", "with-customer");
        repo.Commit("A: Customer");
        repo.Checkout("pr");
        AddEntity(repo, "Dog", "with-dog");
        repo.Commit("B: Dog");
        var bare = repo.Bare();
        var resolver = new MergeQueueResolver(TestRepo.Runner());

        var keyed = await resolver.MergeAsync(bare, "main", "pr", Rules, CancellationToken.None);
        var outcome = await resolver.MergeAsync(bare, "main", "pr", AutoResolveRule.SparkGeneratedFiles, CancellationToken.None);

        keyed.Should().BeOfType<MergeOutcome.Evicted>().Which.Reason.Should().Be(EvictionReason.HunkOutsideAllowedKeys);
        var resolved = outcome.Should().BeOfType<MergeOutcome.Resolved>().Which;
        resolved.Paths.Should().Equal([ModelHashes.Path]);
        TestRepo.Git(bare, "rev-parse", $"{resolved.Tree}:{ModelHashes.Path}").Should().Be(TestRepo.Git(bare, "rev-parse", $"main:{ModelHashes.Path}"));
        TestRepo.Git(bare, "ls-tree", "-r", "--name-only", resolved.Tree).Should().Contain("apps/DemoApp/DemoApp/App_Data/Model/Dog.json");
    }

    [Fact]
    public async Task A_modify_delete_conflict_on_the_hash_file_evicts()
    {
        using var repo = Seed();
        repo.Delete(ModelHashes.Path);
        repo.Commit("A: drop the hash file");
        repo.Checkout("pr");
        AddEntity(repo, "Order", "with-order");
        repo.Commit("B: Order");

        var outcome = await MergeAsync(repo.Bare());

        var evicted = outcome.Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.UnsupportedConflictType);
        evicted.Paths.Should().Equal([ModelHashes.Path]);
        evicted.Detail.Should().Contain("CONFLICT (modify/delete)");
    }

    [Fact]
    public async Task An_add_add_conflict_on_the_roll_ups_only_resolves_to_the_base_side()
    {
        using var repo = new TestRepo();
        repo.Write("README.md", "base\n");
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("main", BaseEntities));
        repo.Commit("A: first synchronize");
        repo.Checkout("pr");
        repo.Write(ModelHashes.Path, ModelHashes.Render("pr", BaseEntities));
        repo.Commit("B: first synchronize");
        var bare = repo.Bare();

        var outcome = await MergeAsync(bare);

        var resolved = outcome.Should().BeOfType<MergeOutcome.Resolved>().Which;
        TestRepo.Git(bare, "rev-parse", $"{resolved.Tree}:{ModelHashes.Path}").Should().Be(TestRepo.Git(bare, "rev-parse", $"main:{ModelHashes.Path}"));
    }

    [Fact]
    public async Task A_side_that_is_not_json_evicts()
    {
        using var repo = Seed();
        AddEntity(repo, "Customer", "with-customer");
        repo.Commit("A: Customer");
        repo.Checkout("pr");
        // Valid line structure, but a trailing comma Spark never writes: not a JSON object, so nothing about its lines
        // can be trusted.
        repo.Write(ModelHashes.Path, ModelHashes.Render("with-order", [.. BaseEntities, "Order"]).Replace("  }\n}\n", "  },\n}\n"));
        repo.Commit("B: Order, broken");

        var outcome = await MergeAsync(repo.Bare());

        outcome.Should().BeOfType<MergeOutcome.Evicted>().Which.Reason.Should().Be(EvictionReason.NotJson);
    }

    [Fact]
    public async Task An_in_repo_gitattributes_cannot_pick_a_merge_driver()
    {
        // The pull request ships attributes that would silently union-merge the hand-authored translations, plus a
        // binary `-merge` and an undefined driver. The writer's runner reads attributes from the empty tree only.
        using var repo = Seed();
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Customer\": { \"en\": \"Customer\" }\n}\n");
        repo.Commit("A: translation");
        repo.Checkout("pr");
        repo.Write(".gitattributes", "* merge=foo\n**/modelHashes.json -merge\n**/translations.json merge=union\n");
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Order\": { \"en\": \"Order\" }\n}\n");
        repo.Commit("B: translation and attributes");
        var bare = repo.Bare();

        // Red: with the pull request's own tree as the attribute source, git union-merges translations.json without a
        // conflict, so a resolver that let in-repo attributes apply would merge a hand-authored file.
        var red = TestRepo.Git(bare, "-c", "attr.tree=pr", "merge-tree", "--write-tree", "--name-only", "--no-messages", "main", "pr");
        red.Should().NotContain("translations.json");

        // Green: the runner's attr.tree is the empty tree, so the conflict stays and the pull request is evicted.
        var outcome = await MergeAsync(bare);

        var evicted = outcome.Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.ConflictOutsideAllowList);
        evicted.Paths.Should().Equal([Translations]);
    }

    [Fact]
    public async Task Merge_tree_output_parses_from_real_runs_with_and_without_conflicts()
    {
        using var repo = Seed();
        AddEntity(repo, "Customer", "with-customer");
        repo.Write("notes.txt", "one\ntwo\nthree\nfour\nfive\n");
        repo.Commit("A");
        repo.Checkout("pr");
        AddEntity(repo, "Order", "with-order");
        repo.Commit("B");
        var bare = repo.Bare();

        var conflicted = MergeTreeOutput.Parse(TestRepo.TryGit(bare, "merge-tree", "--write-tree", "-z", "--messages", "main", "pr").Output);
        conflicted.Stages.Select(s => (s.Stage, s.Path)).Should().Equal([(1, ModelHashes.Path), (2, ModelHashes.Path), (3, ModelHashes.Path)]);
        conflicted.Messages.Select(m => m.Type).Should().Equal(["Auto-merging", "CONFLICT (contents)"]);
        conflicted.Messages[1].Paths.Should().Equal([ModelHashes.Path]);

        // Clean with an auto-merge message: the separator is there and the messages follow it.
        repo.Checkout("main");
        repo.Checkout("side", "main");
        repo.Write("notes.txt", "one\ntwo\nthree\nfour\nfive-side\n");
        repo.Commit("side edit");
        repo.Checkout("main");
        repo.Write("notes.txt", "one-main\ntwo\nthree\nfour\nfive\n");
        repo.Commit("main edit");
        var bare2 = repo.Bare();
        var clean = MergeTreeOutput.Parse(TestRepo.Git(bare2, "merge-tree", "--write-tree", "-z", "--messages", "main", "side"));
        clean.Stages.Should().BeEmpty();
        clean.Messages.Select(m => (m.Type, m.Paths[0])).Should().Equal([("Auto-merging", "notes.txt")]);

        FluentActionsFor.Act(() => MergeTreeOutput.Parse("not a tree\0")).Should().Throw<FormatException>();
    }

    [Fact]
    public async Task A_whole_file_rule_takes_the_base_side_of_any_conflicting_line()
    {
        using var repo = Seed();
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address (main)\" }\n}\n");
        repo.Commit("A: translation");
        repo.Checkout("pr");
        repo.Write(Translations, "{\n  \"Address\": { \"en\": \"Address (pr)\" }\n}\n");
        repo.Commit("B: translation");
        var bare = repo.Bare();
        var resolver = new MergeQueueResolver(TestRepo.Runner());

        // Control: under the O2 key rule (Rules has no rule for translations.json) the same conflict evicts.
        (await resolver.MergeAsync(bare, "main", "pr", [AutoResolveRule.SparkModelHashes, new AutoResolveRule("**/translations.json", ["$schema"])], CancellationToken.None))
            .Should().BeOfType<MergeOutcome.Evicted>().Which.Reason.Should().Be(EvictionReason.HunkOutsideAllowedKeys);

        var outcome = await resolver.MergeAsync(bare, "main", "pr", [AutoResolveRule.WholeFile("**/translations.json")], CancellationToken.None);

        var resolved = outcome.Should().BeOfType<MergeOutcome.Resolved>().Which;
        resolved.Paths.Should().Equal([Translations]);
        TestRepo.Git(bare, "rev-parse", $"{resolved.Tree}:{Translations}").Should().Be(TestRepo.Git(bare, "rev-parse", $"main:{Translations}"));
    }

    [Fact]
    public async Task A_whole_file_rule_still_evicts_a_side_over_the_size_cap_and_needs_no_json()
    {
        using var repo = Seed();
        repo.Write("data/blob.txt", "base\n");
        repo.Commit("blob");
        repo.InWork("branch", "-f", "pr");
        repo.Write("data/blob.txt", new string('m', (int)TakeBaseResolver.MaxBytes + 1));
        repo.Commit("A: big");
        repo.Checkout("pr");
        repo.Write("data/blob.txt", "not json at all\n");
        repo.Commit("B: small");
        var bare = repo.Bare();
        var resolver = new MergeQueueResolver(TestRepo.Runner());

        var outcome = await resolver.MergeAsync(bare, "main", "pr", [AutoResolveRule.WholeFile("data/*.txt")], CancellationToken.None);

        outcome.Should().BeOfType<MergeOutcome.Evicted>().Which.Reason.Should().Be(EvictionReason.TooLarge);
        FluentActionsFor.Act(() => new AutoResolveRule("x.json", [])).Should().Throw<ArgumentException>();
        var rule = AutoResolveRule.WholeFile("data/*.txt");
        rule.AnyLine.Should().BeTrue();
        rule.Keys.Should().BeEmpty();
    }
}
