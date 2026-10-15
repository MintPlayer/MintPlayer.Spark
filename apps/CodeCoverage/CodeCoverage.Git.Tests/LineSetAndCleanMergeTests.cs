using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// R4 of docs/coverage_merge_queue_PRD.md §10.7: the <c>line-set</c> strategy, clean-merge validation and the size caps
/// per strategy, on real repositories (<c>main</c> the base branch, <c>pr</c> the pull request).
/// </summary>
public class LineSetAndCleanMergeTests
{
    private static readonly AutoResolveRule GoSum = AutoResolveRule.Create("**/go.sum", MergeQueueWireRules.LineSet);

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
        => new MergeQueueResolver(TestRepo.Runner()).MergeAsync(bare, "main", "pr", rules, CancellationToken.None);

    [Fact]
    public async Task Line_set_keeps_the_sorted_union_of_both_sides_insertions()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, "go.sum",
            "a v1 h1:a\nm v1 h1:m\n",
            "a v1 h1:a\nc v1 h1:c\nm v1 h1:m\n",
            "a v1 h1:a\nb v1 h1:b\nm v1 h1:m\n");

        var resolved = (await MergeAsync(bare, GoSum)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        TestRepo.Git(bare, "cat-file", "blob", $"{resolved.Tree}:go.sum").Should().Be("a v1 h1:a\nb v1 h1:b\nc v1 h1:c\nm v1 h1:m\n");
        resolved.Resolutions.Should().Equal([new ResolvedPath("go.sum", MergeQueueWireRules.LineSet, false, 0)]);
    }

    [Fact]
    public async Task Line_set_keeps_crlf_and_refuses_a_changed_line()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, "go.sum",
            "a v1 h1:a\r\nm v1 h1:m\r\n",
            "a v1 h1:a\r\nc v1 h1:c\r\nm v1 h1:m\r\n",
            "a v1 h1:a\r\nb v1 h1:b\r\nm v1 h1:m\r\n");
        var resolved = (await MergeAsync(bare, GoSum)).Should().BeOfType<MergeOutcome.Resolved>().Which;
        TestRepo.Git(bare, "cat-file", "blob", $"{resolved.Tree}:go.sum").Should().Be("a v1 h1:a\r\nb v1 h1:b\r\nc v1 h1:c\r\nm v1 h1:m\r\n");

        using var changed = new TestRepo();
        var bare2 = Fixture(changed, "go.sum", "a v1 h1:a\n", "a v2 h1:a2\n", "a v3 h1:a3\n");
        (await MergeAsync(bare2, GoSum)).Should().BeOfType<MergeOutcome.Evicted>().Which.Reason.Should().Be(EvictionReason.ResolverRefused);
    }

    [Fact]
    public async Task A_clean_merge_that_breaks_a_validator_both_sides_pass_is_evicted()
    {
        using var repo = new TestRepo();
        // Both sides add "x", far enough apart for git to merge them without a conflict: valid JSON, a duplicate member.
        var bare = Fixture(repo, "config/app.json",
            "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3,\n  \"d\": 4\n}\n",
            "{\n  \"x\": 1,\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3,\n  \"d\": 4\n}\n",
            "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3,\n  \"x\": 2,\n  \"d\": 4\n}\n");

        // Control: without a validating rule the merge is clean.
        (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Clean>();

        var evicted = (await MergeAsync(bare, AutoResolveRule.Create("**/*.json", MergeQueueWireRules.KeepBoth, [MergeQueueWireRules.JsonNoDuplicateKeys])))
            .Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.MergedContentInvalid);
        evicted.Paths.Should().Equal(["config/app.json"]);
        evicted.Detail.Should().Contain(MergeQueueWireRules.JsonNoDuplicateKeys);

        // An evict rule masks it: no validators run for that path.
        (await MergeAsync(bare, AutoResolveRule.Create("config/app.json", MergeQueueWireRules.Evict),
            AutoResolveRule.Create("**/*.json", MergeQueueWireRules.KeepBoth, [MergeQueueWireRules.JsonNoDuplicateKeys]))).Should().BeOfType<MergeOutcome.Clean>();
    }

    [Fact]
    public async Task Keep_both_refuses_a_side_over_its_four_MiB_cap_where_take_base_takes_it()
    {
        using var repo = new TestRepo();
        var big = string.Concat(Enumerable.Range(0, 170_000).Select(i => $"line {i:D20}\n")); // 4.42 MB
        var bare = Fixture(repo, "data/list.txt", "start\nend\n", "start\nmain\nend\n" + big, "start\npr\nend\n");

        var evicted = (await MergeAsync(bare, AutoResolveRule.Create("data/*.txt", MergeQueueWireRules.KeepBoth))).Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.TooLarge);
        evicted.Detail.Should().Contain(KeepBothResolver.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        (await MergeAsync(bare, AutoResolveRule.WholeFile("data/*.txt"))).Should().BeOfType<MergeOutcome.Resolved>();
    }
}
