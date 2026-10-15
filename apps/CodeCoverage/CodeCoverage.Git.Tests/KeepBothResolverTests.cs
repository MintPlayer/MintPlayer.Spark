using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// keep-both (docs/coverage_merge_queue_PRD.md O33, §10.4, R3) on real repositories: every merge is a real merge-tree run
/// and every region a real <c>git merge-file --diff3</c> one. <c>main</c> is the base branch, <c>pr</c> the pull request.
/// </summary>
public class KeepBothResolverTests
{
    private const string Security = "apps/DemoApp/DemoApp/App_Data/security.json";

    private static readonly AutoResolveRule JsonRule = AutoResolveRule.Create("**/*.json", MergeQueueWireRules.KeepBoth, [MergeQueueWireRules.JsonNoDuplicateKeys]);

    /// <summary>Commits <paramref name="base"/>, then <paramref name="main"/> on main and <paramref name="pr"/> on pr.</summary>
    private static string Fixture(TestRepo repo, string path, string @base, string main, string pr)
    {
        repo.Write(path, @base);
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(path, main);
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(path, pr);
        repo.Commit("B");
        return repo.Bare();
    }

    private static Task<MergeOutcome> MergeAsync(string bare, params AutoResolveRule[] rules)
        => new MergeQueueResolver(TestRepo.Runner()).MergeAsync(bare, "main", "pr", rules.Length == 0 ? [JsonRule] : rules, CancellationToken.None);

    private static string Blob(string bare, string tree, string path) => TestRepo.Git(bare, "cat-file", "blob", $"{tree}:{path}");

    /// <summary>A right as Spark writes it: five lines inside the array, three of them shared by every right of a group.</summary>
    private static string Right(string id, bool last = false)
        => $"    {{\n      \"id\": \"{id}\",\n      \"resource\": \"Read/{id}\",\n      \"groupId\": \"g1\",\n      \"isDenied\": false\n    }}{(last ? "" : ",")}\n";

    private static string Rights(params string[] ids)
        => "{\n  \"rights\": [\n" + string.Concat(ids.Select((id, i) => Right(id, i == ids.Length - 1))) + "  ]\n}\n";

    [Fact]
    public async Task One_line_entries_inserted_into_one_gap_keep_both_base_branch_first()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security,
            "{\n  \"rights\": [\n    \"A\",\n    \"C\"\n  ]\n}\n",
            "{\n  \"rights\": [\n    \"A\",\n    \"B-main\",\n    \"C\"\n  ]\n}\n",
            "{\n  \"rights\": [\n    \"A\",\n    \"B-pr\",\n    \"C\"\n  ]\n}\n");

        var resolved = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        Blob(bare, resolved.Tree, Security).Should().Be("{\n  \"rights\": [\n    \"A\",\n    \"B-main\",\n    \"B-pr\",\n    \"C\"\n  ]\n}\n");
        resolved.Resolutions.Should().Equal([new ResolvedPath(Security, MergeQueueWireRules.KeepBoth, false, 0)]);
    }

    /// <summary>
    /// The zdiff3 regression guard (§10.4 step 1): two five-line rights in one gap share their groupId, isDenied and
    /// brace lines. zdiff3 lifts those out of the region, so taking both regions would give ONE object with two ids
    /// (syntactically fine for plain json). Plain diff3 keeps each right whole.
    /// </summary>
    [Fact]
    public async Task Five_line_rights_inserted_into_one_gap_stay_two_whole_objects()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security, Rights("A", "C"), Rights("A", "B1", "C"), Rights("A", "B2", "C"));

        var resolved = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        Blob(bare, resolved.Tree, Security).Should().Be(Rights("A", "B1", "B2", "C"));
    }

    [Fact]
    public async Task Two_object_members_inserted_into_one_gap_are_both_kept()
    {
        using var repo = new TestRepo();
        const string path = "apps/DemoApp/DemoApp/App_Data/translations.json";
        var bare = Fixture(repo, path,
            "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Person\": { \"en\": \"Person\" }\n}\n",
            "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Customer\": { \"en\": \"Customer\" },\n  \"Person\": { \"en\": \"Person\" }\n}\n",
            "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Order\": { \"en\": \"Order\" },\n  \"Person\": { \"en\": \"Person\" }\n}\n");

        var resolved = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        Blob(bare, resolved.Tree, path).Should().Be(
            "{\n  \"Address\": { \"en\": \"Address\" },\n  \"Customer\": { \"en\": \"Customer\" },\n  \"Order\": { \"en\": \"Order\" },\n  \"Person\": { \"en\": \"Person\" }\n}\n");
    }

    [Fact]
    public async Task Both_sides_adding_one_member_name_is_an_invalid_result_naming_validator_and_position_only()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security,
            "{\n  \"a\": 1,\n  \"c\": 3\n}\n",
            "{\n  \"a\": 1,\n  \"secret\": \"main-value\",\n  \"c\": 3\n}\n",
            "{\n  \"a\": 1,\n  \"secret\": \"pr-value\",\n  \"c\": 3\n}\n");

        var evicted = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Evicted>().Which;

        evicted.Reason.Should().Be(EvictionReason.InvalidResult);
        evicted.Paths.Should().Equal([Security]);
        evicted.Detail.Should().Contain(MergeQueueWireRules.JsonNoDuplicateKeys);
        evicted.Detail.Should().Contain("line 4");
        evicted.Detail.Should().NotContain("main-value");

        // Control: without the validator the same keep-both is written (the validator is what stops it).
        (await MergeAsync(bare, AutoResolveRule.Create("**/*.json", MergeQueueWireRules.KeepBoth))).Should().BeOfType<MergeOutcome.Resolved>();
    }

    [Fact]
    public async Task A_conflict_that_changes_a_base_line_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security, "{\n  \"a\": 1\n}\n", "{\n  \"a\": 2\n}\n", "{\n  \"a\": 3\n}\n");

        var evicted = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Evicted>().Which;

        evicted.Reason.Should().Be(EvictionReason.ResolverRefused);
        evicted.Detail.Should().Contain("merge base");
    }

    [Fact]
    public async Task Appending_after_a_one_line_last_member_changes_its_comma_and_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security,
            "{\n  \"a\": 1\n}\n",
            "{\n  \"a\": 1,\n  \"b\": 2\n}\n",
            "{\n  \"a\": 1,\n  \"c\": 3\n}\n");

        (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Evicted>().Which.Reason.Should().Be(EvictionReason.ResolverRefused);
    }

    [Fact]
    public async Task An_add_add_conflict_is_refused()
    {
        using var repo = new TestRepo();
        repo.Write("README.md", "base\n");
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(Security, "{\n  \"a\": 1\n}\n");
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(Security, "{\n  \"b\": 2\n}\n");
        repo.Commit("B");

        var evicted = (await MergeAsync(repo.Bare())).Should().BeOfType<MergeOutcome.Evicted>().Which;

        evicted.Reason.Should().Be(EvictionReason.ResolverRefused);
        evicted.Detail.Should().Contain("add/add");
    }

    [Fact]
    public async Task A_side_with_a_marker_like_line_is_refused()
    {
        using var repo = new TestRepo();
        var rule = AutoResolveRule.Create("**/*.md", MergeQueueWireRules.KeepBoth);
        var bare = Fixture(repo, "CHANGELOG.md",
            "Title\n=======\n\n- one\n",
            "Title\n=======\n\n- main\n- one\n",
            "Title\n=======\n\n- pr\n- one\n");

        var evicted = (await MergeAsync(bare, rule)).Should().BeOfType<MergeOutcome.Evicted>().Which;

        evicted.Reason.Should().Be(EvictionReason.ResolverRefused);
        evicted.Detail.Should().Contain("conflict marker");
    }

    [Fact]
    public async Task Crlf_line_endings_are_kept_byte_for_byte()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security,
            "{\r\n  \"rights\": [\r\n    \"A\",\r\n    \"C\"\r\n  ]\r\n}\r\n",
            "{\r\n  \"rights\": [\r\n    \"A\",\r\n    \"B-main\",\r\n    \"C\"\r\n  ]\r\n}\r\n",
            "{\r\n  \"rights\": [\r\n    \"A\",\r\n    \"B-pr\",\r\n    \"C\"\r\n  ]\r\n}\r\n");

        var resolved = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        Blob(bare, resolved.Tree, Security).Should().Be("{\r\n  \"rights\": [\r\n    \"A\",\r\n    \"B-main\",\r\n    \"B-pr\",\r\n    \"C\"\r\n  ]\r\n}\r\n");
    }

    [Fact]
    public async Task Resolving_twice_gives_the_same_tree()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Security, Rights("A", "C"), Rights("A", "B1", "C"), Rights("A", "B2", "C"));

        var first = (MergeOutcome.Resolved)await MergeAsync(bare);
        var second = (MergeOutcome.Resolved)await MergeAsync(bare);

        second.Tree.Should().Be(first.Tree);
    }

    [Fact]
    public async Task A_side_that_already_fails_a_validator_refuses_before_the_result_is_judged()
    {
        using var repo = new TestRepo();
        // Every side has a trailing comma: the result cannot be blamed on keep-both (inputs first).
        var bare = Fixture(repo, Security,
            "{\n  \"a\": 1,\n  \"c\": 3,\n}\n",
            "{\n  \"a\": 1,\n  \"b1\": 2,\n  \"c\": 3,\n}\n",
            "{\n  \"a\": 1,\n  \"b2\": 2,\n  \"c\": 3,\n}\n");

        var evicted = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Evicted>().Which;

        evicted.Reason.Should().Be(EvictionReason.InvalidSide);
        evicted.Detail.Should().Contain("already fails json-no-duplicate-keys");
    }
}
